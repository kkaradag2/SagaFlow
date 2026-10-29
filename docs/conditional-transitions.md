# YAML conditional transitions

Transitions accept `on` as an alias for the existing `event` field. Use one per transition.
Canonical discovery YAML continues to write `event`. The existing unconditional YAML and
legacy Boolean expression format remain supported.

```yaml
- name: inventory
  command: ReserveInventory
  compensation:
    command: ReleaseInventory
  transitions:
    - on: success
      when:
        path: context.totalQuantity
        operator: greaterThan
        value: 1000
      nextStep: managerNotification
    - on: success
      nextStep: shipment
    - on: failure
      nextStep: compensation
```

Only transitions for the received `success` or `failure` outcome are considered.
Structured conditions are evaluated in YAML order; the first match wins. A transition
without `when` is the fallback even when listed before conditions. At most one fallback
is allowed per outcome. `else: true` remains an explicit fallback. `timeout` transitions
remain definition metadata under the existing timeout policy.

## Values and paths

The four operators are `equals`, `notEquals`, `greaterThan`, and `lessThan`.
Equality accepts JSON strings, numbers, Booleans, and null; ordered comparisons require
a numeric YAML value and numeric runtime value. Types are never coerced. A type mismatch
does not match, including `notEquals`. Strings use ordinal, case-sensitive comparison.
JSON numbers are compared mathematically without decimal/double rounding or overflow.
Use JSON scalar syntax for numeric/Boolean literals; quote values such as `"70"` and
`"true"` when they are strings. Arrays, objects, and explicit YAML scalar tags are unsupported.

Paths access existing runtime data:

- `context.customer.flags.requiresShipping`: the original Saga payload.
- `steps.payment.result.risk.score`: an accepted step outcome already stored in `Steps`.
- `result.accepted`, or `steps.<currentStep>.result.accepted`: the outcome being evaluated.

For example:

```yaml
when:
  path: steps.payment.result.risk.score
  operator: greaterThan
  value: 70
```

Property names match the actual JSON exactly. Saga payload serialization uses camelCase;
outcome property casing comes from its producer. Nested object properties are supported.
Each dot-separated segment uses ASCII letters/digits/underscore/hyphen and starts with a
letter or underscore. Paths are limited to 512 characters. A step reference must name a
declared step. Array indexing, escaped dots, AND/OR, calls, and arbitrary expressions are
unsupported. Missing properties, unproduced results, and traversing a non-object do not
match. Missing is distinct from an existing JSON null.

The coordinator records the outcome result and status on its atomic working snapshot
before evaluating transitions. `steps.<currentStep>.result.*` reads that recorded result.
Result recording and next-step selection commit together; an invalid transition leaves
the store unchanged. Evaluation does not merge results into `Context`, mutate JSON, or
create another result store. For C# callers, structured YAML
`when` maps to `SagaTransitionDefinition.Condition` (`SagaConditionDefinition`). The
existing string `When` property remains the legacy Boolean expression API. Supply one
format per transition. Definition snapshots clone structured comparison values.

If no condition matches and there is no fallback for the outcome, the coordinator
preserves its existing rejection semantics: it throws an explicit error before accepting
the outcome, leaving the dispatched step and Saga revision unchanged. It does not
silently select another outcome category or dispatch a command. Structured rules may
omit a fallback, but applications should normally define one.

Legacy scalar expressions such as `when: "result.available == true"` retain their
original Boolean-only grammar, result root, static exclusivity/coverage validation, and
strict missing/non-Boolean result errors. Use the structured format for the new path
sources, ordered comparisons, overlapping first-match rules, and missing-path fallback.

## Order sample

`Order.TotalQuantity` is computed from all item quantities and serialized into the Saga
context. It is not an API request field. Orders with quantity <= 1000 follow payment →
inventory → shipment → notification. Larger orders follow payment → inventory → manager
notification → manager approval and wait for an API decision. Approval continues to
shipment and notification; rejection is a successful business outcome that selects
compensation in YAML. See the [Manager Approval sample](manager-approval-sample.md).

Any accepted outcome selecting `nextStep: compensation` starts the compensation plan,
including a successful outcome such as `{ "approved": false }`. The originating step
keeps its successful `Completed` state and result.

Compensation remains sequential and reverses the actual successful forward execution order.
An unexecuted step stays `Pending`, without input, result, execution order, or timings,
and never receives compensation. The Dashboard displays these pending steps as
`Not executed` after forward execution ends, without adding a new persisted step status.
The Domain Starter card and the existing SignalR refresh path are retained.
