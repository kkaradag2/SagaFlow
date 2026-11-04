# Thesis Mapping

- Declarative Process Definition -> `SagaFlow.Core.Definitions`, `SagaFlow.Engine.ProcessDefinition`
- Local Coordination -> `SagaFlow.Engine.Coordination`
- Messaging Interaction -> `SagaFlow.Core.Messages`, `SagaFlow.Engine.Messaging`
- Context and State Management -> `SagaFlow.Core.State`, `SagaFlow.Engine.ContextState`
- Consistency Management -> `SagaFlow.Engine.Consistency`
- Observability and Visibility -> `SagaFlow.Dashboard`, `SagaFlow.Dashboard.Client`

`SagaFlow.Hosting` provides the in-process ASP.NET Core integration layer for embedding SagaFlow into a domain starter microservice. The representative prototype consists of the `SagaFlow.Order` Domain Starter and the independent Payment, Inventory, Shipment, and Notification participant workers under `samples/`.

Return PoC eklemesi: [`SagaFlow.Return`](../samples/ReturnProcess/SagaFlow.Return/Program.cs) ikinci, bağımsız Domain Starter'dır; [`SagaFlow.Warehouse`](../samples/Participants/SagaFlow.Warehouse/Program.cs) insan kararı veren participant'tır. Payment, Inventory ve Notification iki süreçte de participant olarak kullanılır; bunlara koordinatör rolü verilmez. [Return teknik belgesi](07-return-workflow.md) komutları ve sınırları açıklar.

BigPicture process ilişkisi uzantısı: [`Order YAML`](../samples/OrderProcess/SagaFlow.Order/saga/order-process.yaml) Return'a `related` adlı tek yönlü, statik tanım ilişkisi bildirir; [`DefinitionDiscovery`](../src/SagaFlow.Engine/Discovery/DefinitionDiscovery.cs) ve [`BigPictureGraph`](../src/SagaFlow.Dashboard.Client/src/components/BigPicture/BigPictureGraph.tsx) bunu görünür kılar. Bu, S2 için iki bağımsız domain tanımı arasındaki beyan edilmiş bağlantıyı ve S6 için statik keşif görünürlüğünü artırır. Domainler arası yürütüm koordinasyonu, runtime Saga correlation ve birleşik trace hâlâ kanıtlanmamıştır; S2/S6 `Partial` kalır. [Tanım keşfi belgesi](03-yaml-definition-discovery.md) sözleşme ve demo adımlarını içerir.

Bu liste modül-sorumluluk eşlemesidir; uygulanma veya tez senaryosu doğrulaması iddiası değildir. Güncel altı sorumluluk durumu, S1/S2/S3/S6/S7/S9 senaryoları, test sonuçları ve referans mimari farkları için [01-PROJECT_STATUS.md](01-PROJECT_STATUS.md) esas alınır. `samples/Participants/SagaFlow.ManagerApproval` insan kararı örneğini ekler; [örnek belgesi](06-manager-approval-sample.md) kapsamını açıklar.
