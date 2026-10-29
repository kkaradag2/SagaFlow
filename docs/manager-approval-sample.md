# Manager Approval sample

## Implementation files

- New participant project: `samples/Participants/SagaFlow.ManagerApproval/` contains
  `SagaFlow.ManagerApproval.csproj`, `Program.cs`, `appsettings.json`,
  `Properties/launchSettings.json`, `Domain/ApprovalRecord.cs`,
  `Repositories/InMemoryApprovalRepository.cs`, `Messaging/ApprovalCommandHandler.cs`,
  `Messaging/RabbitMqApprovalOutcomePublisher.cs`, `Services/ApprovalDecisionService.cs`,
  and `Controllers/ApprovalsController.cs`.
- Order sample: `Domain/OrderModels.cs`, `Services/OrderService.cs`,
  `Validators/CreateOrderRequestValidator.cs`, and `saga/order-process.yaml` replace the
  delivery-method example with the computed quantity and approval branch.
- Shared messaging: `samples/Participants/Shared/RabbitMqParticipantWorker.cs` supports
  deferred outcomes, original ReplyTo, and multiple queues. Existing Payment, Inventory,
  and Shipment handler signatures accept optional ReplyTo; their operations are retained.
- Notification: `Domain/NotificationModels.cs`, `Messaging/NotificationCommandHandler.cs`,
  `Services/INotificationService.cs`, `Services/NotificationService.cs`,
  `Repositories/INotificationRepository.cs`, `Repositories/InMemoryNotificationRepository.cs`,
  and `appsettings.json` add the manager notification and its queue.
- Engine: `Coordination/SagaCoordinator.cs`, `Coordination/ConditionEvaluator.cs`,
  `Coordination/TransitionResolver.cs`, and `ContextState/ProcessStateManager.cs` record
  results before evaluation and start compensation for successful business decisions.
- Dashboard: `src/SagaFlow.Dashboard.Client/src/pages/InstancesPage.tsx` and the generated
  embedded dashboard bundle add the explicit outcome-waiting message.
- Tests: new `ManagerApprovalTransitionTests.cs` and `ManagerApprovalTests.cs`; updated
  `RuntimeTransitionTests.cs`, `StructuredConditionalTransitionTests.cs`, `OrderSampleTests.cs`,
  `ParticipantFailureInjectionTests.cs`, `SampleRabbitMqEndToEndTests.cs`, both affected test
  project files, and `SagaFlow.slnx`. This guide and `docs/conditional-transitions.md`
  describe the final sample.

The authoritative [Order YAML](../samples/OrderProcess/SagaFlow.Order/saga/order-process.yaml)
retains Payment, Inventory, Shipment, and final Notification and adds two optional steps:

| Step / queue | Command | Participant |
| --- | --- | --- |
| `payment` | `AuthorizePayment` / `RefundPayment` | Payment |
| `inventory` | `ReserveInventory` / `ReleaseInventory` | Inventory |
| `managerNotification` | `NotifyManager` | Notification |
| `managerApproval` | `RequestManagerApproval` | ManagerApproval |
| `shipment` | `CreateShipment` / `CancelShipment` | Shipment |
| `notification` | `SendOrderNotification` | Notification |

`Order.TotalQuantity` sums `items[].quantity` using `long`. It is computed from the domain
items, serialized into the original Saga payload, and cannot be supplied as a request
field. The previous deliveryMethod/shipping/pickup sample is removed.

Inventory routes in YAML using `context.totalQuantity`, `greaterThan`, and `value: 1000`:

- Quantity 999 or 1000: Payment → Inventory → Shipment → Notification → Completed.
- Quantity 1001 or greater: Payment → Inventory → NotifyManager → ManagerApproval.

Notification consumes both `notification` and `managerNotification`. Manager messages are
stored separately from final customer notifications and print Order ID and total quantity
to the console once. Both commands return ordinary success outcomes. There is no real
email/SMS integration and neither notification operation has a compensation command.

## Running locally

Use the existing RabbitMQ compose file and run the sample services in separate terminals:

```powershell
docker compose -f samples/docker-compose.yml up -d
dotnet run --project samples/OrderProcess/SagaFlow.Order
dotnet run --project samples/Participants/SagaFlow.Payment
dotnet run --project samples/Participants/SagaFlow.Inventory
dotnet run --project samples/Participants/SagaFlow.Shipment
dotnet run --project samples/Participants/SagaFlow.Notification
dotnet run --project samples/Participants/SagaFlow.ManagerApproval
```

ManagerApproval's launch profile uses `http://localhost:5090`; Swagger is available at
`http://localhost:5090/swagger` in Development. It references Core and the shared sample
participant messaging code, and registers no SagaFlow coordinator or local definition.

Create a large order through the existing Order API (HTTP launch-profile port 50824):

```http
POST http://localhost:50824/orders
Content-Type: application/json

{
  "customerId": "CUST-TEST-001",
  "items": [
    { "productId": "PRODUCT-001", "quantity": 600, "unitPrice": 100 },
    { "productId": "PRODUCT-002", "quantity": 401, "unitPrice": 10 }
  ]
}
```

Use the returned `orderId` in these ManagerApproval endpoints:

```http
GET http://localhost:5090/approvals/pending
GET http://localhost:5090/approvals/{orderId}

POST http://localhost:5090/approvals/{orderId}/decision
Content-Type: application/json

{ "decision": "approve" }
```

Submit `{ "decision": "reject" }` for rejection. Both decisions publish **success**
outcomes, with `{ "approved": true }` or `{ "approved": false }` respectively. Technical
participant failures still use failure outcomes. Responses:

| HTTP status | Meaning |
| --- | --- |
| 200 | Read succeeded, or decision outcome received publisher confirmation |
| 400 | Decision is not exactly `approve` or `reject` |
| 404 | No approval exists for the Order ID |
| 409 | Approval has an active/terminal/uncertain publication, a retry changes its decision, or Order ID is ambiguous |
| 503 | Outcome publication failed or its delivery could not be confirmed |

The in-memory repository keys records by `(SagaInstanceId, StepId)`. Duplicate commands
with identical metadata return the original record without resetting it or sending an
outcome. Conflicting duplicate metadata is rejected. Different Sagas retain independent
records; multiple records for the same Order ID conservatively return 409 from lookup and
decision endpoints, even if only one remains pending. This PoC does not add disambiguation.

## Deferred outcome and state transitions

1. `RequestManagerApproval` arrives on the `managerApproval` queue via the existing
   `sagaflow.commands` direct exchange and step-name routing.
2. The handler records Order ID, total quantity, SagaInstanceId, CorrelationId, StepId,
   original ReplyTo, and request time. The shared worker ACKs without publishing an outcome.
3. The Saga remains `Running`, with managerApproval `Dispatched` and no result.
4. The decision API atomically claims `Pending → Publishing`, retaining the decision.
   Competing requests return 409. Network I/O runs outside the repository lock.
5. The publisher uses the original ReplyTo (normally `order.outcomes`), original headers,
   persistent messages, mandatory routing, publisher confirms, and the shared stable
   `{saga}:{step}:outcome` MessageId. It does not choose the next step or compensation.
6. On confirmation the approval becomes `Approved` or `Rejected`. Connection/channel or
   other setup failures before `BasicPublishAsync` is invoked become `PublishNotSent` and
   return 503. Only the same recorded decision may be retried manually through the decision
   endpoint. A failure after entering `BasicPublishAsync` remains `PublishFailed` and cannot
   be retried automatically or by resubmitting the endpoint. Both states retain the decision
   and error. HTTP disconnection after claim does not cancel finalization; publication has
   its own 10s bound.

On accepted outcome, the Engine first records managerApproval as `Completed` with its
result on the same atomic snapshot used by YAML evaluation:

```yaml
- on: success
  when:
    path: steps.managerApproval.result.approved
    operator: equals
    value: true
  nextStep: shipment
- on: success
  nextStep: compensation
- on: failure
  nextStep: compensation
```

Approve continues Shipment → Notification → Saga Completed. Reject keeps managerApproval
`Completed` with `approved: false`, dispatches ReleaseInventory, waits for its outcome,
then dispatches RefundPayment, waits, and runs Order's existing local rollback. Saga becomes
Compensated only after rollback completes. Shipment and final Notification stay unexecuted.
Neither manager step has a fabricated domain compensation operation.

A final Notification failure on a normal order or after approval still compensates
CancelShipment → ReleaseInventory → RefundPayment → Order rollback.

## Waiting policy, Dashboard, and PoC limits

managerApproval deliberately has **no timeoutLimit**: its RabbitMQ command has no TTL and
the Engine timeout detector has no deadline for the human decision. The 10s publication
bound applies only after an API decision; it is not a human response deadline. Existing
timeouts and retry metadata for other steps are unchanged. No new workflow timeout/retry
mechanism is introduced.

The existing Dashboard keeps its Domain Starter card and combined forward/compensation
rows. An active dispatched step displays that it is waiting for an outcome and, when
applicable, has no configured timeout. Decision JSON remains in the normal result panel.
Unused steps keep `Pending` with no execution data and display `Not executed` after
forward execution ends. SignalR refresh behavior is unchanged.

Pending approvals and publication states are process-local. Restarting ManagerApproval
after its command has been ACKed loses that record; it does not recover the waiting Saga.
There is no database, outbox, recovery process, automatic resend, authentication system,
or additional manager UI. Swagger is the sample decision surface.

`ApprovalOutcomeNotSentException` is produced only before `BasicPublishAsync` has been
invoked. The repository permits `PublishNotSent → Publishing` for an identical decision,
preserving its original decision time and routing/correlation metadata. A different decision
or a concurrent request returns 409; command redelivery does not reset the record. There is
no background retry. GET by Order ID and the 503 response expose the retryable state.

A lost publisher confirmation can mean the outcome was already delivered. `PublishFailed`
therefore still blocks all further submissions instead of resetting to Pending or silently
republishing. Unknown exceptions are conservatively treated as delivery-uncertain. Inspect
the stored decision/error via GET by Order ID and the Saga Dashboard; manual reconciliation
is required for uncertainty. The PoC does not claim durable or exactly-once delivery.

## Verification

Engine tests use the actual Order YAML for thresholds 999/1000/1001, the indefinite pending
step, approval/rejection, result recording, and technical failure. Sample tests cover API
statuses, correlation/ReplyTo, redelivery, ambiguity, concurrent decisions, publish failure,
original participants, local rollback, and the normal/approved/rejected routes.

```powershell
dotnet test SagaFlow.slnx
dotnet build SagaFlow.slnx
```

The existing opt-in RabbitMQ E2E tests also exercise a real pending approval, approve,
reject, and approval followed by Notification failure. To run against the existing broker:

```powershell
$env:SAGAFLOW_SAMPLE_RABBITMQ_INTEGRATION = "true"
dotnet test tests/SagaFlow.Samples.Tests --filter FullyQualifiedName~SampleRabbitMqEndToEndTests
```
