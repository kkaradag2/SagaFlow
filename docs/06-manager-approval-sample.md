# Manager Approval sample

## S2-7A Procurement → Order PoC

Uygulama `Implemented`; ayrı OS process deneysel değerlendirme S2-7B `Planned`. [`ProcurementProgram`](../samples/ProcurementProcess/SagaFlow.Procurement/Program.cs), [`PurchaseRequestsController`](../samples/ProcurementProcess/SagaFlow.Procurement/Controllers/PurchaseRequestsController.cs) ve [`procurement-process.yaml`](../samples/ProcurementProcess/SagaFlow.Procurement/saga/procurement-process.yaml) yeni Domain Starter'dır. Procurement kendi business kaydını ve Saga'sını oluşturur; mevcut ManagerApproval participant'ına komut gönderir. Approved → external InvokeOrder → Order terminal sonucu → kendi YAML geçişi; Rejected → business Rejected, invocation yok. Duplicate approval ikinci çağrı oluşturmaz. Domain Starter'lar birbirinin store'una erişmez; merkezi coordinator veya senkron domainler arası HTTP bağımlılığı yoktur.

ManagerApproval bir participant olarak kalır. Business command body'sindeki `id` ve repository/API'deki `OrderId` adı Procurement kararında **PurchaseRequestId** taşır; gerçek OrderId henüz oluşmamıştır. ReplyTo `procurement.outcomes`, SagaInstanceId ve correlation kaynak komuttan korunur. Manuel approve/reject ve mevcut 409/delivery-uncertain davranışları değişmez. Order toplam miktarı >1000 ise kendi mevcut managerApproval adımı ayrıca çalışır; o karar gerçek OrderId ile verilir.

Order business acceptance ve OrderId oluşturma yalnız [`IncomingOrderService`](../samples/OrderProcess/SagaFlow.Order/Services/IncomingOrderService.cs) içindedir. Order kendi existing validator/creation yolunu, Saga'sını ve participant akışını kullanır. Saf policy business reject'i Saga/Order yaratmadan döndürür; opsiyonel generic initializer accepted claim üzerinde Order kaydını üretir. [Kabul/materialization sırası ve atomiklik sınırları](04-local-saga-coordination.md#s2-7a-target-application-acceptance-ve-business-materialization).

### İki ayrı OS process ile yerel başlatma

S2-7B1 manuel deneyleri için sample step timeout ayarları kaldırılmıştır (`Implemented`): Order'ın payment, inventory, managerNotification, shipment ve notification adımlarındaki `timeoutLimit: 10s` silindi; Procurement ve Return zaten timeout tanımlamıyordu. Participant çalışmazsa Saga sonucu beklemeye devam edebilir; komutun step timeout'undan türeyen TTL'si de yoktur. Bu, üretim için önerilen timeout/recovery politikası değildir. SagaFlow timeout detector'ı ve timeout özelliği korunur; YAML timeout transition'ları, compensation/rollback ve diğer tanımlar değiştirilmedi. Kanıt: [`OrderSampleTests.OrderYamlUsesCurrentSchemaAndLoadsSuccessfully`](../tests/SagaFlow.Samples.Tests/OrderSampleTests.cs), [`SagaTimeoutDetectorTests.StepWithoutTimeoutLimitIsNeverAffected`](../tests/SagaFlow.Engine.Tests/SagaTimeoutDetectorTests.cs). Değişiklik yalnız yeniden yüklenen tanımlar/yeni instance'lar için geçerlidir; mevcut TimedOut instance'ları kurtarmaz. Order hostunu kullanıcı kontrollü olarak yeniden başlatın ve yeni purchase request/onay akışı oluşturun. Participant'ları önceden hazır etmek normal başarı akışını kolaylaştırır. Önceden başlamış Procurement instance'ı yeni request ile otomatik ilişkilendirilmez; restart sırasında InMemory state kaybı sınırını dikkate alın.

Repo kökünden mevcut Compose broker'ını kullanın; her `dotnet run` ayrı terminal/process'te çalışır. .NET 10 SDK ve Docker gerekir. HTTPS discovery için mevcut geliştirme sertifikasını güvenilir yapın (`dotnet dev-certs https --trust`). Broker credential/vhost override'ları **tüm terminallerde aynı** olmalıdır; örnek varsayılan `sagaflow` / `sagaflow-dev`, vhost `/`. Test runner üretim `/` vhost'unu kullanmaz.

```powershell
docker compose -f samples/docker-compose.yml up -d --wait
dotnet build SagaFlow.slnx
dotnet run --project samples/Participants/SagaFlow.Payment
dotnet run --project samples/Participants/SagaFlow.Inventory
dotnet run --project samples/Participants/SagaFlow.Shipment
dotnet run --project samples/Participants/SagaFlow.Notification
dotnet run --project samples/Participants/SagaFlow.ManagerApproval --launch-profile SagaFlow.ManagerApproval
dotnet run --project samples/OrderProcess/SagaFlow.Order --launch-profile SagaFlow.Order.Procurement
dotnet run --project samples/ProcurementProcess/SagaFlow.Procurement --launch-profile SagaFlow.Procurement
```

Order HTTPS 50823 / HTTP 50824, Procurement HTTPS 50831 / HTTP 50832; ManagerApproval HTTP 5090. Her uygulamanın DI container'ı, InMemory Saga store'u ve business repository'si ayrıdır. Her iki Dashboard kendi `/sagaflow` yolundadır. Procurement discovery Order'ın HTTPS 50823 gRPC origin'ini allowlist ile çözer; `invokes` definition kenarı ve InvocationId üzerinden runtime Source/Target ilişkileri mevcut S2-6 API/UI ile sunulur. Order'ın mevcut Return `related` ilişkisi korunur; Return çalışmıyorsa veya Procurement trust listesine dahil değilse bu ek kenar unresolved gösterilir. Return keşfi gerekiyorsa mevcut Return profilini başlatıp `SagaFlow__Discovery__AllowedEndpoints__1=https://localhost:65305` ekleyin; runtime Order invocation buna bağlı değildir.

Başlangıçta karşı domainin gRPC/HTTP erişimine ihtiyaç yoktur; keşif graph sorgusu sırasında yapılır. RabbitMQ ulaşılabilir olmalıdır. Order/Procurement başlangıç sırası sabit değildir; participant/target consumer hazır değilse broker kuyruğu kullanılır. Kaynak/target host restart recovery garantisi verilmez. Test, Order'ı önce başlatır; farklı OS process başlangıç sırası deneyi S2-7B'de doğrulanacaktır.

Ortam override örneği: `RabbitMq__HostName`, `RabbitMq__Port`, `RabbitMq__UserName`, `RabbitMq__Password`, `RabbitMq__VirtualHost`; target Order için `RabbitMq__EnableProcessInvocations=true`, `SagaFlow__PublishDefinitions=true`, `SagaFlow__PublishBigPictureSources=true` yeni profile dahildir. Procurement bunları kendi appsettings'inden açar. Profil kullanılmadan çalıştırılırsa `ASPNETCORE_URLS`, `ASPNETCORE_ENVIRONMENT=Development`, ilgili SagaFlow ayarları ve proje dizinine göre `SagaFlow__DefinitionDirectory` verilebilir. Başka port seçilirse YAML endpoint ve discovery allowlist birlikte değiştirilir; gRPC endpoint'i Dashboard URL resolver değildir.

### Manuel istek, karar ve izleme

```powershell
$purchase = Invoke-RestMethod http://localhost:50832/purchase-requests -Method Post -ContentType application/json -Body '{"employeeId":"employee-1","customerId":"company-1","items":[{"productId":"laptop","quantity":2,"unitPrice":1250}]}'
Invoke-RestMethod "http://localhost:5090/approvals/$($purchase.purchaseRequestId)"
Invoke-RestMethod "http://localhost:5090/approvals/$($purchase.purchaseRequestId)/decision" -Method Post -ContentType application/json -Body '{"decision":"approve"}'
$view = Invoke-RestMethod "http://localhost:50832/purchase-requests/$($purchase.purchaseRequestId)"
Invoke-RestMethod "http://localhost:50824/orders/invocations/$($view.invocationId)"
```

`reject` Order çağırmaz. API boş employee/customer, boş/geçersiz items, nonpositive quantity/price, toplam overflow ve bilinmeyen FailureAt değerini 400 ile reddeder. Participant hata deneyi için purchase JSON'a `"failureAt":"notification"` eklenebilir: gerçek Order compensation → Procurement business Failed. Order Failed terminal testi local rollback fault'u yalnız test fixture'ında üretir; üründe ek failure endpoint'i yoktur. Yerel ilk dispatch başarısızsa POST 503 içinde purchaseRequestId döner, mevcut yerel kayıt GET ile incelenir; otomatik iş isteği replay'i eklenmedi.

PurchaseRequestId, OrderId, kaynak/hedef SagaInstanceId, InvocationId ve kaynak/hedef CorrelationId birbirinden ayrıdır. Purchase GET yalnız kaynak metadata ve son bilinen Order status'unu gösterir; gerçek OrderId Order'ın invocation lookup endpoint'indedir. Source last-known ile target local status aynı otorite değildir. Her iki store, business idempotency eşlemesi ve pending approval restart'ta kaybolur; cross-store business exactly-once, durable inbox/outbox veya recovery iddiası yoktur.

Kanıt: [`ProcurementDomainTests`](../tests/SagaFlow.Samples.Tests/ProcurementDomainTests.cs), [`ProcurementRabbitMqTests`](../tests/SagaFlow.Samples.Tests/ProcurementRabbitMqTests.cs), [`IncomingInvocationInitializationTests`](../tests/SagaFlow.Engine.Tests/IncomingInvocationInitializationTests.cs). Güncel test sonuçları [proje durumunda](01-PROJECT_STATUS.md). S2-7A, gerçek brokerla aynı test process'indeki iki Kestrel uygulamasını ve ayrı participant worker process'lerini doğrular. Procurement/Order'ın ayrı OS process'lerde kapsamlı başarı/hata/duplicate/sıralama/izlenebilirlik ve başlangıç bağımsızlığı ölçümü ile yeni sample'ın browser kabulü S2-7B'ye bırakılmıştır. Deneysel rapor/tez sonucu yazılmadı; S2 `Partial` kalır.

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
  project files, and `SagaFlow.slnx`. This guide and `docs/05-conditional-transitions.md`
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

## Typed participant business requests

Sample refactoring `Implemented` (2026-11-05). All six participant handlers use sample-local [`ParticipantBinding.Bind<T>`](../samples/Participants/Shared/ParticipantBinding.cs); they no longer navigate business JSON with GetProperty/TryGetProperty. The AMQP v1 parser and business-only wire body are unchanged. No Core/Engine/Hosting or participant SDK dependency was added. Request IDs come from business JSON, never SagaInstanceId/correlation/execution metadata.

| Participant | Request records / unchanged JSON fields |
| --- | --- |
| [Payment](../samples/Participants/SagaFlow.Payment/Messaging/BusinessRequests.cs) | AuthorizePaymentRequest (`id`, `total`); RefundPaymentRequest (`id`); RefundReturnPaymentRequest (`returnId`, `orderId`, `amount`) |
| [Inventory](../samples/Participants/SagaFlow.Inventory/Messaging/BusinessRequests.cs) | ReserveInventoryRequest (`id`, `items`); ReleaseInventoryRequest (`id`); RestockReturnRequest (`returnId`, `orderId`, `items`). Items reuse InventoryLine (`productId`, `quantity`). |
| [Shipment](../samples/Participants/SagaFlow.Shipment/Messaging/BusinessRequests.cs) | CreateShipmentRequest (`id`, `customerId`); CancelShipmentRequest (`id`). Existing sample still passes customerId to the shipment service's address parameter. |
| [Notification](../samples/Participants/SagaFlow.Notification/Messaging/BusinessRequests.cs) | SendOrderNotificationRequest (`id`, `customerId`); NotifyManagerRequest (`id`, `totalQuantity`); NotifyReturnRequest (`returnId`, `orderId`, `customerId`) shared by accepted/rejected commands |
| [ManagerApproval](../samples/Participants/SagaFlow.ManagerApproval/Messaging/BusinessRequests.cs) | RequestManagerApprovalRequest (`id`, `totalQuantity`); Procurement's `id` remains PurchaseRequestId, as described above |
| [Warehouse](../samples/Participants/SagaFlow.Warehouse/BusinessRequests.cs) | InspectReturnRequest (`returnId`, `orderId`) |

Binding uses System.Text.Json with case-insensitive property matching, required constructor parameters and nullable annotation enforcement. Numeric strings are not coerced. Unknown fields are ignored so the existing full Order/Return payload can be consumed through command-specific subsets. Null/undefined/non-object bodies, missing required fields, null required values and invalid scalar/nested types produce JsonException with command/model context. Existing service rules still validate amounts, quantities, products and recipients; Inventory checks null collection entries at the typed boundary because serializer nullable enforcement does not cover array elements. Business success/failure result shapes and existing worker failure-outcome handling remain intact; the protocol parser still handles malformed AMQP metadata separately.

Optional failureAt is bound through SampleFailureRequest. The shared converter preserves the previous rule that non-string failureAt does not request failure; participant values retain ordinal matching. Existing early failure returns stay before required business binding. This is sample fault injection, not a new validation/retry framework.

To add another sample command, define its business-only record in that participant and bind at the existing handler boundary, for example:

```csharp
public sealed record AuthorizePaymentRequest(Guid Id, decimal Total);

var request = command.Bind<AuthorizePaymentRequest>();
return ParticipantCommandResult.Success(
    payments.AuthorizePayment(request.Id.ToString("N"), request.Total));
```

Keep command selection and compensation guards in the handler, existing business validation in the service, and SagaFlow identity/ReplyTo in transport context. Add the command to CommandNames and existing queue configuration; cover missing/type/null binding and existing business results. ManagerApproval and Warehouse continue registering the original command with DeferredReplies and create only typed business records in its callback; decision services receive business IDs/decisions, not execution headers or opaque transport handles. Binding does not change the original JSON snapshot used for deferred conflict detection.

Evidence: [`ParticipantBindingTests`](../tests/SagaFlow.Samples.Tests/ParticipantBindingTests.cs), existing ParticipantDomainTests/ParticipantFailureInjectionTests/ManagerApprovalTests/ReturnDomainTests, real deferred wire and Procurement E2E regressions. Actual run totals and explicit unrun checks are in [project status](01-PROJECT_STATUS.md). This remains optional sample application infrastructure, not a published participant SDK; all business/deferred state remains InMemory.

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
`http://localhost:5090/swagger` in Development. It references only the shared sample RabbitMQ infrastructure and RabbitMQ.Client; it has no Core/Engine/Hosting dependency and registers no SagaFlow coordinator or local definition. See the [BPI-1A protocol](04-local-saga-coordination.md#bpi-1a-dil-bağımsız-participant-wire-sözleşmesi).

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

BPI-1A-01 remediation: the business repository is keyed by **ApprovalId**, and business records contain OrderId, quantity, decision/timestamps and publication status. SagaInstanceId, CorrelationId, StepId, ReplyTo, ParticipantContext and transport handles are absent from both the model and API JSON. Hiding these fields with JsonIgnore is not the isolation mechanism. Multiple approval records for the same OrderId retain the existing conservative 409 ambiguity behavior; ApprovalId is a participant business identity, not a Saga execution ID.

The sample-local [`DeferredReplies`](../samples/Participants/Shared/DeferredReplies.cs) transport store maps each accepted execution to an opaque `DeferredReplyHandle`, original execution headers/ReplyTo and the associated business record ID. The messaging handler registers the command as a whole; the decision service sends only ApprovalId and the business decision to the publisher. The RabbitMQ publisher resolves the transport handle and constructs the outcome. Redelivery cannot reset the business record; conflicting execution context, ReplyTo or input is rejected in transport infrastructure. These maps and business records remain in memory and do not survive restart. [`DeferredMetadataIsolationTests`](../tests/SagaFlow.Samples.Tests/DeferredMetadataIsolationTests.cs) checks model types, JSON and handle isolation; [`DeferredWireContractTests`](../tests/SagaFlow.Samples.Tests/DeferredWireContractTests.cs) checks two ManagerApproval and two Warehouse executions against four original reply queues.

Missing/unsupported wire versions are preserved in `managerApproval.quarantine` before business handling, with mandatory/confirm publication preceding source ACK. A transfer failure stops worker consumption without a hot requeue loop; restore the quarantine destination and restart the worker. Confirm/ACK uncertainty may leave multiple inspectable copies. Existing v1 malformed-message behavior remains separate. See [quarantine and cutover policy](04-local-saga-coordination.md#bpi-1a-02-quarantine-ve-operasyonel-geçiş) and [`ParticipantQuarantineTests`](../tests/SagaFlow.Samples.Tests/ParticipantQuarantineTests.cs).

## Deferred outcome and state transitions

1. `RequestManagerApproval` arrives on the `managerApproval` queue via the existing
   `sagaflow.commands` direct exchange and step-name routing.
2. The messaging handler records business ApprovalId, OrderId, total quantity and request time through the business repository. The separate transport registry captures the whole opaque execution context/ReplyTo and associates its handle with ApprovalId. The shared worker ACKs without publishing an outcome; no technical fields are added to the business record.
3. The Saga remains `Running`, with managerApproval `Dispatched` and no result.
4. The decision API atomically claims `Pending → Publishing`, retaining the decision.
   Competing requests return 409. Network I/O runs outside the repository lock.
5. The publisher resolves the opaque transport handle by ApprovalId and uses the original ReplyTo (normally `order.outcomes`), original headers,
   persistent messages, mandatory routing, publisher confirms, and its own outcome MessageId. The body is only `{ "approved": true/false }`; `sf-outcome=success` and `sf-causation-id=command.MessageId` are transport headers. It does not choose the next step or compensation.
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
bound applies only after an API decision; it is not a human response deadline. S2-7B1
removes the other sample step deadlines for manual experiments as described above;
retry metadata and the Engine timeout mechanism remain unchanged.

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
