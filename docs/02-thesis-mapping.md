# Thesis Mapping

- Declarative Process Definition -> `SagaFlow.Core.Definitions`, `SagaFlow.Engine.ProcessDefinition`
- Local Coordination -> `SagaFlow.Engine.Coordination`
- Messaging Interaction -> `SagaFlow.Core.Messages`, `SagaFlow.Engine.Messaging`
- Context and State Management -> `SagaFlow.Core.State`, `SagaFlow.Engine.ContextState`
- Consistency Management -> `SagaFlow.Engine.Consistency`
- Observability and Visibility -> `SagaFlow.Dashboard`, `SagaFlow.Dashboard.Client`

`SagaFlow.Hosting` provides the in-process ASP.NET Core integration layer for embedding SagaFlow into a domain starter microservice. The representative prototype consists of the `SagaFlow.Order` Domain Starter and the independent Payment, Inventory, Shipment, and Notification participant workers under `samples/`.

Return PoC eklemesi: [`SagaFlow.Return`](../samples/ReturnProcess/SagaFlow.Return/Program.cs) ikinci, bağımsız Domain Starter'dır; [`SagaFlow.Warehouse`](../samples/Participants/SagaFlow.Warehouse/Program.cs) insan kararı veren participant'tır. Payment, Inventory ve Notification iki süreçte de participant olarak kullanılır; bunlara koordinatör rolü verilmez. [Return teknik belgesi](07-return-workflow.md) komutları ve sınırları açıklar.

Bu liste modül-sorumluluk eşlemesidir; uygulanma veya tez senaryosu doğrulaması iddiası değildir. Güncel altı sorumluluk durumu, S1/S2/S3/S6/S7/S9 senaryoları, test sonuçları ve referans mimari farkları için [01-PROJECT_STATUS.md](01-PROJECT_STATUS.md) esas alınır. `samples/Participants/SagaFlow.ManagerApproval` insan kararı örneğini ekler; [örnek belgesi](06-manager-approval-sample.md) kapsamını açıklar.
