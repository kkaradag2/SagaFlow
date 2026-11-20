# Thesis Mapping

S2-7A Procurement → Order PoC (2026-11-05) `Implemented`: [`ProcurementProgram`](../samples/ProcurementProcess/SagaFlow.Procurement/Program.cs) ve kendi [`YAML`](../samples/ProcurementProcess/SagaFlow.Procurement/saga/procurement-process.yaml), Domain Starter / Participant ayrımını somut iş sürecine uygular. ManagerApproval yeniden kullanılır; approved external step mevcut InvokeAsync ile Order'ı çağırır, rejected çağırmaz. Order saf application policy ile business acceptance uygular; opsiyonel generic [`IIncomingInvocationInitializer`](../src/SagaFlow.Engine/Messaging/IIncomingInvocationInitializer.cs) üzerinden kendi business kaydını üretir. Aynı creation/validation yolu doğrudan Order API ile paylaşılır; mevcut participant akışı ve Engine terminal/source continuation kullanılır. İki domain kendi state'ini yönetir; business ve teknik kimlikler ayrıdır, merkezi coordinator yoktur. Kanıt: [`ProcurementDomainTests`](../tests/SagaFlow.Samples.Tests/ProcurementDomainTests.cs), [`ProcurementRabbitMqTests`](../tests/SagaFlow.Samples.Tests/ProcurementRabbitMqTests.cs), [`IncomingInvocationInitializationTests`](../tests/SagaFlow.Engine.Tests/IncomingInvocationInitializationTests.cs). İzole tam .NET **495/495** (önceki 484 + 11), Dashboard **93/93**, build **0 warning/error**; [güncel doğrulama kaydı](01-PROJECT_STATUS.md). Gerçek broker/HTTP/gRPC testindeki iki ana uygulama aynı OS process'indedir; participant worker'ları ayrı process'tir. Ayrı Procurement/Order OS process launch hazırlığı tamam, kapsamlı deneysel değerlendirme ve bu business sample'ın browser kabulü S2-7B `Planned`; S2/S6 `Partial`. InMemory business idempotency ve atomik Saga claim, cross-store exactly-once/restart recovery garantisi değildir. [Acceptance/kimlik sınırları](04-local-saga-coordination.md#s2-7a-target-application-acceptance-ve-business-materialization), [yerel çalıştırma](06-manager-approval-sample.md#s2-7a-procurement--order-poc). Aşağıdaki eski S2-7 Planned kayıtları kendi tarihsel aşamalarını anlatır; PoC uygulaması artık Implemented, deneysel değerlendirme Planned'dır.

S2-6C BigPicture real E2E/browser acceptance (2026-11-05): local visibility `Implemented`, verified using real RabbitMQ + HTTP/gRPC and Chrome. [`BigPictureAcceptanceTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/BigPictureAcceptanceTests.cs) covers target execution/source continuation, invocation identity and isolation, null metadata, sticky reconciliation and actual relationship APIs. The two main independent IHost/container/store/broker connections run in the **same OS process**. [`verify-bigpicture-ux.mjs`](../scripts/verify-bigpicture-ux.mjs) verifies real graph/Drawer/navigation/clipboard/SignalR/reconnect/stale-response UX; ignored screenshots were visually inspected separately. The 101-row pagination fixture is seeded and is not normal broker E2E evidence. A minimal SVG label-overpainting fix stays in the presentation layer. This supersedes earlier S2-6B notes marking browser acceptance Planned. Definition declarations remain separate from instance observations; Source last-known and Target local status remain distinct, and each API reads only one locally owned instance. S2/S6 stay `Partial`: InMemory/restart and federation limits remain; S2-7 Procurement → Order experimental PoC is `Planned`. [Acceptance evidence and boundaries](03-yaml-definition-discovery.md#s2-6c-real-rabbitmq--http--chrome-acceptance).

S2-6B BigPicture Graph & Runtime Relationship UX (2026-11-05) `Implemented`: [`BigPicturePage`](../src/SagaFlow.Dashboard.Client/src/pages/BigPicturePage.tsx), [`BigPictureGraph`](../src/SagaFlow.Dashboard.Client/src/components/BigPicture/BigPictureGraph.tsx) ve [`useInstanceRelationships`](../src/SagaFlow.Dashboard.Client/src/live/useInstanceRelationships.ts), mevcut statik definition graph'ına opsiyonel, URL ile korunan tek yerel Instance Context ekler. Observability/Visibility: related/invokes ayrımı, yerel invocation vurgusu ve aynı Drawer'da kimlik/status/reconciliation okunması; execution veya merkezi koordinasyon değildir. Source last-known remote status ve Target local status ayrı kalır; yalnız bilinen yerel instance için navigasyon vardır. Sayfalama/eksik sonuç açıklaması, stale request isolation ve mevcut host-local SignalR kullanılır. Kanıt: [`bigPictureRuntime.test.tsx`](../src/SagaFlow.Dashboard.Client/tests/bigPictureRuntime.test.tsx), [`bigPictureGraph.test.tsx`](../src/SagaFlow.Dashboard.Client/tests/bigPictureGraph.test.tsx), [`workflowNavigation.test.tsx`](../src/SagaFlow.Dashboard.Client/tests/workflowNavigation.test.tsx). DOM/API mock testleri gerçek broker browser kabulü değildir; yeni BigPicture akışının S2-6C browser/E2E kabulü `Planned`. S2/S6 `Partial`; global index/federation, durable recovery veya Procurement sample eklenmedi. [Görünüm, navigasyon ve S2-6C sınırları](03-yaml-definition-discovery.md#s2-6b-definition-view-and-instance-context-ux). Önceki S2-6A kaydındaki B Planned ifadesi bu yeni kayıtla güncellenir.

S2-6A BigPicture relationship backend (2026-11-05) `Implemented`: mevcut definition discovery statik `related`/`invokes` yönünü ve resolved/unresolved metadata'sını sunmayı sürdürür. [`InstanceRelationshipView`](../src/SagaFlow.Hosting/Dashboard/InstanceRelationshipView.cs), mevcut source invocation ve target incoming state'ini tek yerel instance üzerinden salt okunur runtime ilişki DTO'suna yansıtır. Bu, Observability/Visibility sorumluluğudur; yeni execution/coordinator/registry değildir. Source `lastKnownStatus` uzak otoriter state değildir; target `targetLocalStatus` yalnız kendi domaininin state'idir. Invocation ID korunur, correlation'lar birleştirilmez. Mevcut host-local SignalR event sonrası aynı API yeniden okunur. Kanıt: [`InstanceRelationshipApiTests`](../tests/SagaFlow.Hosting.Tests/InstanceRelationshipApiTests.cs), [`InstanceTraceabilityApiTests`](../tests/SagaFlow.Hosting.Tests/InstanceTraceabilityApiTests.cs), gerçek iki-host HTTP/RabbitMQ [`InstanceUxAcceptanceTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/InstanceUxAcceptanceTests.cs). S2/S6 `Partial`: InMemory/restart, federation ve recovery sınırları sürer; S2-6B istemci grafik/Drawer ve S2-6C sample/uçtan uca kabul `Planned`. [S2-6B için API sözleşmesi](03-yaml-definition-discovery.md#s2-6a-bigpicture-relationship-backend). Aşağıdaki aşama kayıtlarındaki eski Planned ifadeleri kendi tarihsel kapsamlarıyla okunmalıdır.

- Declarative Process Definition -> `SagaFlow.Core.Definitions`, `SagaFlow.Engine.ProcessDefinition`
- Local Coordination -> `SagaFlow.Engine.Coordination`
- Messaging Interaction -> `SagaFlow.Core.Messages`, `SagaFlow.Engine.Messaging`, `SagaFlow.Messaging.RabbitMQ`
- Context and State Management -> `SagaFlow.Core.State`, `SagaFlow.Engine.ContextState`
- Consistency Management -> `SagaFlow.Engine.Consistency`
- Observability and Visibility -> `SagaFlow.Dashboard`, `SagaFlow.Dashboard.Client`

BPI-1A (`Implemented`, participant messaging kapsamı): [`SagaMessageHeaders`](../src/SagaFlow.Core/Messages/SagaMessageHeaders.cs) transport-independent execution context'ini, Engine atomik dispatch/outcome eşlemesini, [`RabbitMqMessageSerializer`](../src/SagaFlow.Messaging.RabbitMQ/RabbitMqMessageSerializer.cs) business-only JSON ve AMQP properties/header ayrımını sağlar. Altı participant SagaFlow runtime/SDK bağımlılığı taşımaz; sample-local helper yalnız RabbitMQ protokolü uygular. Deferred/compensation, reply routing, version/legacy cutover ve invocation'ın ayrı kapsamı [wire sözleşmesinde](04-local-saga-coordination.md#bpi-1a-dil-bağımsız-participant-wire-sözleşmesi); gerçek testler ve açık dayanıklılık sınırları [doğrulama kaydında](01-PROJECT_STATUS.md#bpi-1a-dogrulama). Bu uygulama S2 deneysel sonuçlarını veya InMemory/recovery sınırlarını tamamlanmış saymaz. BPI-1A review düzeltmesi, business modelden teknik alan/context/handle'ı çıkarıp [`DeferredReplies`](../samples/Participants/Shared/DeferredReplies.cs) transport association'ına taşır; legacy wire için confirmed quarantine ve iki gerçek consumer ile atomik duplicate yarış kanıtı ekler. [Review testleri ve operasyonel sınırlar](01-PROJECT_STATUS.md#bpi-1a-review-remediation).

`SagaFlow.Hosting` provides the in-process ASP.NET Core integration layer for embedding SagaFlow into a domain starter microservice. The representative prototype consists of the `SagaFlow.Order` Domain Starter and the independent Payment, Inventory, Shipment, and Notification participant workers under `samples/`.

Return PoC eklemesi: [`SagaFlow.Return`](../samples/ReturnProcess/SagaFlow.Return/Program.cs) ikinci, bağımsız Domain Starter'dır; [`SagaFlow.Warehouse`](../samples/Participants/SagaFlow.Warehouse/Program.cs) insan kararı veren participant'tır. Payment, Inventory ve Notification iki süreçte de participant olarak kullanılır; bunlara koordinatör rolü verilmez. [Return teknik belgesi](07-return-workflow.md) komutları ve sınırları açıklar.

BigPicture process ilişkisi uzantısı: [`Order YAML`](../samples/OrderProcess/SagaFlow.Order/saga/order-process.yaml) Return'a `related` adlı tek yönlü, statik tanım ilişkisi bildirir; [`DefinitionDiscovery`](../src/SagaFlow.Engine/Discovery/DefinitionDiscovery.cs) ve [`BigPictureGraph`](../src/SagaFlow.Dashboard.Client/src/components/BigPicture/BigPictureGraph.tsx) bunu görünür kılar. Bu, S2 için iki bağımsız domain tanımı arasındaki beyan edilmiş bağlantıyı ve S6 için statik keşif görünürlüğünü artırır. Domainler arası yürütüm koordinasyonu, runtime Saga correlation ve birleşik trace hâlâ kanıtlanmamıştır; S2/S6 `Partial` kalır. [Tanım keşfi belgesi](03-yaml-definition-discovery.md) sözleşme ve demo adımlarını içerir.

S2-1 sözleşme temeli `Partial`: [`ISagaFlow`](../src/SagaFlow.Core/Abstractions/ISagaFlow.cs) `InvokeAsync<TPayload>(target, payload)` imzasını, [`ProcessInvocation`](../src/SagaFlow.Core/Messages/ProcessInvocation.cs) ayrı kaynak/hedef kimliği, request, accepted/rejected, terminal outcome, receipt ve iki tarafta saklanabilecek instance ilişki biçimini tanımlar. [`ProcessInvocationResolver`](../src/SagaFlow.Engine/Coordination/ProcessInvocationResolver.cs) kaynak metadata'yı aktif yerel instance ve dispatched step'ten türetir; yalnız açık `invokes` hedefi ve sürümünü çözer. [`SagaCoordinator`](../src/SagaFlow.Engine/Coordination/SagaCoordinator.cs) review düzeltmesi sonrası yalnız handler scope içinde yerel `Prepared` receipt üretir; mesaj gönderme, uzak kabul veya tamamlama yoktur. [Engine sözleşme testleri](../tests/SagaFlow.Engine.Tests/ProcessInvocationContractTests.cs) çözümleme ve izolasyonu sınar; gerçek domainler arası yürütüm doğrulanmadı, S2 `Partial` kalır.

Bu liste modül-sorumluluk eşlemesidir; uygulanma veya tez senaryosu doğrulaması iddiası değildir. Güncel altı sorumluluk durumu, S1/S2/S3/S6/S7/S8/S9 senaryoları, test sonuçları ve referans mimari farkları için [01-PROJECT_STATUS.md](01-PROJECT_STATUS.md) esas alınır. `samples/Participants/SagaFlow.ManagerApproval` insan kararı örneğini ekler; [örnek belgesi](06-manager-approval-sample.md) kapsamını açıklar.

S2-1 review context/identity refinement: [`LocalSagaExecutionBoundary`](../src/SagaFlow.Hosting/DependencyInjection/LocalSagaExecutionBoundary.cs) supplies a fresh scoped Domain Starter handler execution from the real step executor. [`SagaCoordinator`](../src/SagaFlow.Engine/Coordination/SagaCoordinator.cs) atomically validates stored instance/step and registers an immutable step invocation. Prepared receipts describe local registration only. Stable IDs, broker independent message identity/correlation and contract invariants are implemented; cross-domain messaging/durable recovery remain Planned. See [execution model](03-yaml-definition-discovery.md#s2-1-invocation-contracts-and-review-resolution). S2 remains Partial.

S2-2 yerel state uzantısı `Implemented`: [`ProcessInvocationManager`](../src/SagaFlow.Engine/Coordination/ProcessInvocationManager.cs) Context and State Management sorumluluğunda invocation progress ve hedef provenance kayıtlarını atomik yönetir; [`SagaCoordinator`](../src/SagaFlow.Engine/Coordination/SagaCoordinator.cs) bunu mevcut handler scope ve InvokeAsync'e bağlar. [`InvocationStateTests`](../tests/SagaFlow.Engine.Tests/InvocationStateTests.cs) yerel store davranışını sınar. Bu çalışma domainler arası mesaj teslimini veya iki store arasında atomikliği kanıtlamaz; S2 `Partial`, S2-3 messaging ve S2-4 uzak sonucu bekleme `Planned`. [Yaşam döngüsü ve sınırlar](04-local-saga-coordination.md#s2-2-invocation-lifecycle-ve-instance-ilişkileri).

S2-3 messaging uzantısı `Implemented` (opt-in): [`IProcessInvocationBus`](../src/SagaFlow.Core/Abstractions/IProcessInvocationBus.cs), [`ProcessInvocationPublisher`](../src/SagaFlow.Engine/Messaging/ProcessInvocationPublisher.cs), [`ProcessInvocationReceiver`](../src/SagaFlow.Engine/Messaging/ProcessInvocationReceiver.cs) ve [`RabbitMqInvocationTransport`](../src/SagaFlow.Messaging.RabbitMQ/RabbitMqInvocationTransport.cs) Messaging Interaction ile yerel Context/State sorumluluklarını birleştirir. [`ProcessInvocationIntegrationTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ProcessInvocationIntegrationTests.cs) ayrı Domain Starter hostlarında gerçek broker request/acceptance/rejection ve explicit terminal yayınını doğrular. Target forward yürütümü ve source uzak-sonuç geçişi S2-4'e kalır. S2 `Partial`: durable teslimat/recovery, dağıtık exactly-once veya birleşik runtime trace iddiası yoktur. [Güncel messaging semantiği](04-local-saga-coordination.md#s2-3-cross-domain-messaging-ve-outcome-propagation).

S2-4 Local Coordination Integration `Implemented`: [`SagaCoordinator`](../src/SagaFlow.Engine/Coordination/SagaCoordinator.cs) target ilk-dispatch claim'i, genel terminal lifecycle bildirimi ve source External step continuation'ını mevcut transition/compensation/rollback mekanizmalarıyla yürütür. [`SagaStepDefinition.External`](../src/SagaFlow.Core/Definitions/SagaStepDefinition.cs) opt-in bekleme semantiğidir; static invokes tek başına çalışma başlatmaz. [`CrossDomainCoordinationTests`](../tests/SagaFlow.Engine.Tests/CrossDomainCoordinationTests.cs) ve [`ProcessInvocationIntegrationTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ProcessInvocationIntegrationTests.cs) yerel atomiklik/duplicate/reconciliation ile gerçek iki-host broker zincirini sınar. S2 `Partial`: durable recovery/transaction yok; S2-5/S2-6 instance ve BigPicture runtime görünürlüğü geliştirilmedi. [Güncel yürütüm semantiği](04-local-saga-coordination.md#s2-4-target-execution-ve-source-external-step-continuation).

S2-5 Instance Dashboard traceability `Implemented`: [`SagaFlowDashboardEndpoints`](../src/SagaFlow.Hosting/Endpoints/SagaFlowDashboardEndpoints.cs) yerel source invocation/target incoming ilişkilerini allowlist DTO ile sunar; [`InstanceTraceability`](../src/SagaFlow.Dashboard.Client/src/components/InstanceTraceability.tsx) local/external status, origin/correlation ve açık reconciliation'ı ayrı gösterir. Mevcut SignalR post-commit bildirimleri kullanılır. [`InstanceTraceabilityApiTests`](../tests/SagaFlow.Hosting.Tests/InstanceTraceabilityApiTests.cs) ve gerçek brokerlı [`ProcessInvocationIntegrationTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ProcessInvocationIntegrationTests.cs) iki hostun yerel API ownership sınırını doğrular. UI DOM testleri ve final gerçek Chrome UX kabulü geçti. Gerçek RabbitMQ kullanan iki ayrı hostun HTTP Dashboard API kayıtlarında multiple invocation izolasyonu ve eksik metadata, [InstanceUxAcceptanceTests](../tests/SagaFlow.Messaging.RabbitMQ.Tests/InstanceUxAcceptanceTests.cs) ve [Playwright browser assertion'ları](../scripts/verify-instance-ux.mjs) ile doğrulandı; önceki Computer Use engeli tarihsel kayıtta korunur. S2 `Partial`; S2-6 BigPicture runtime/federation ve durable recovery geliştirilmedi. [Alan ve sınır semantiği](04-local-saga-coordination.md#s2-5-yerel-instance-dashboard-traceability).

BPI-1B: invocation adapter business request JSON / null lifecycle body ile sf-inv-* metadata ayrımı sağlar; internal Engine result yeniden tasarımı BPI-1C'ye kalır. [Wire mapping ve sınırlar](04-local-saga-coordination.md#bpi-1b-cross-domain-invocation-wire).

BPI-1C: external coordination completion business Result üretmez; step status/timestamp güncellenir, teknik observation/rejection mevcut InvocationProgress/read-model alanlarında kalır. Participant/compensation business JSON korunur. [Semantik ve kanıtlar](04-local-saga-coordination.md#bpi-1c-business-result-ve-coordination-state). BPI-1D bütüncül kabul Planned. MSG-01 (`Implemented`): external `result.*` ve `steps.<external>.result[.*]` koşulları mevcut path/parser üzerinden reddedilir; normal participant/context koşulları korunur. Bypass edilmiş kalıcı tanım/transition hatasında teknik observation kaybolmadan local Failed + reconciliation commit edilir; geçerli terminal ACK edilir ve duplicate continuation üretmez. Bu bir genel retry/recovery veya coordination-query dili değildir. Kanıt: [`ExternalStepConditionTests`](../tests/SagaFlow.Engine.Tests/ExternalStepConditionTests.cs), [`ExternalConditionRuntimeTests`](../tests/SagaFlow.Engine.Tests/ExternalConditionRuntimeTests.cs), gerçek broker [`ExternalConditionRedeliveryTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ExternalConditionRedeliveryTests.cs); [güncel test kaydı](01-PROJECT_STATUS.md).

## NewSample iskeleti

[NewSample](../NewSample/NewSample.slnx), mevcut prototipin yanında üç bağımsız Domain Starter ve altı katılımcı için uygulama iskeletini içerir. Order, Return ve Procurement .NET 10 ASP.NET Core hostlarıdır. Payment, Inventory, Shipment, ManagerApproval ve Warehouse .NET 10 konsollarıdır. [Notification](../NewSample/Participants/Notification/main.py), `python NewSample/Participants/Notification/main.py` ile çalıştırılan bağımsız Python konsoludur.

.NET proje adları mimarideki rolleri izler. Domain Starter'lar `Sample.DomainStarters.<Domain>`, katılımcılar `Sample.Participants.<Participant>` biçimini kullanır.

Order kendi Saga tanımını yükler, PostgreSQL durum deposunu ve RabbitMQ taşımasını kaydeder ve Dashboard'u sunar. Payment ve Inventory, Order Saga'nın ödeme ve stok komutlarını RabbitMQ üzerinden işler. Diğer katılımcıların mesaj akışı ve gRPC keşfi sonraki uygulama aşamasındadır. [Derleme ve başlangıç doğrulaması](01-PROJECT_STATUS.md#newsample-iskeleti).

### NewSample yerel altyapısı

Depo kökünden `docker compose -f NewSample/compose.yaml up -d --wait` ile [altyapıyı](../NewSample/compose.yaml) başlatın. Paylaşılan PostgreSQL instance'ında Order, Payment ve Inventory kendi veritabanlarını kullanır. Return ve Procurement veritabanları ileride eklenecek.

| Servis | Varsayılan yerel adres | Kullanıcı |
|---|---|---|
| PostgreSQL | `127.0.0.1:55433` | `postgres` |
| pgAdmin | `http://127.0.0.1:5051` | `admin@example.com` |
| RabbitMQ | `amqp://127.0.0.1:5673` | `newsample` |
| RabbitMQ yönetimi | `http://127.0.0.1:15673` | `newsample` |
| Grafana | `http://127.0.0.1:4300` | `admin` |
| Collector OTLP | gRPC `http://127.0.0.1:14317`, HTTP `http://127.0.0.1:14318` | |

pgAdmin'de `NewSample PostgreSQL` bağlantısı hazır gelir. Bağlanırken PostgreSQL parolasını girin. Yerel geliştirme parolaları ve ortam değişkenleri için [Compose yapılandırmasına](../NewSample/compose.yaml) bakın.

[OpenTelemetry Collector](../NewSample/infrastructure/observability/otel-collector.yaml) logları Loki'ye, trace'leri Tempo'ya gönderir. Prometheus, Collector'ın sunduğu metrikleri çeker. [Grafana kaynakları](../NewSample/infrastructure/observability/grafana-datasources.yaml) başlangıçta tanımlanır. Order servis logları Collector'a OTLP/gRPC ile gönderilir.

### NewSample Order API

[Order veritabanı betiği](../NewSample/infrastructure/postgres/init-order.sql) yeni PostgreSQL volume'unda otomatik çalışır. Var olan volume'da ilk kurulum veya eski veritabanını yerinde yeniden adlandırmak için depo kökünden aynı betiği uygulayın. Ad değişikliğinden önce eski veritabanındaki oturumları kapatın.

```powershell
docker compose -f NewSample/compose.yaml exec -T postgres psql -U postgres -d postgres -f /docker-entrypoint-initdb.d/01-init-order.sql
dotnet run --project NewSample/DomainStarters/Order
```

[Order controller](../NewSample/DomainStarters/Order/Controllers/OrdersController.cs), `POST http://localhost:5176/orders` isteğini `IOrderService` üzerinden işler. Örnek istek gövdesi aşağıdadır.

Development ortamında [Swagger UI](http://localhost:5176/swagger) üzerinden de istek gönderebilirsiniz. [OpenAPI belgesi](http://localhost:5176/swagger/v1/swagger.json) aynı profilde sunulur.

```json
{
  "customerId": "customer-1",
  "items": [
    { "productId": "sku-1", "quantity": 2, "unitPrice": 49.90 }
  ]
}
```

Order ve kalemleri aynı veritabanı işleminde yazılır. Sipariş commit edildikten sonra servis SagaFlow'u çağırır. `201 Created` yanıtı `orderId`, `sagaInstanceId`, toplam ve sipariş durumunu içerir. SagaFlow başlangıç çağrısı hata verirse API siparişi koruyup 503 ile `orderId` ve biliniyorsa `sagaInstanceId` döndürür. [`IOrderService.CancelAsync`](../NewSample/DomainStarters/Order/Services/IOrderService.cs) compensation için durumu `Cancelled` yapar, kalemleri ve ilk iptal zamanını korur.

[Order SagaFlow kaydı](../NewSample/DomainStarters/Order/Orchestration/OrderSagaFlowRegistration.cs), [önceki Order örneğinden taşınan YAML tanımını](../NewSample/DomainStarters/Order/saga/order-process.yaml) yükler. Sipariş verileri `ordering`, SagaFlow durumu ise aynı `Order` veritabanındaki `sagaflow` şemasında tutulur. RabbitMQ bağlantısı `ConnectionStrings:RabbitMq` üzerinden kurulur. [SagaFlow Dashboard](http://localhost:5176/sagaflow/) tanımı ve başlayan Saga örneklerini gösterir. Return tanım keşfi sonraki aşamadadır.

SagaFlow, RabbitMQ outcome'u compensation gerektirdiğinde önce varsa katılımcı telafilerini tamamlar. Ardından Order hostunda [Order rollback handler](../NewSample/DomainStarters/Order/Services/OrderRollbackHandler.cs) üzerinden `IOrderService.CancelAsync` çağrılır. Başarılı yerel iptalde Dashboard'da Domain Rollback `Completed`, Saga `Compensated` görünür. POST tekrarlarını yönetme ve sipariş kaydı ile Saga başlangıcı arasındaki hatalardan kurtarma PoC kapsamının dışındadır.

SagaFlow.Hosting NuGet paketi Dashboard dosyalarını derleme ve publish çıktısındaki `wwwroot/dashboard` dizinine kopyalar. SagaFlow bu dosyaları `/sagaflow` altında sunar. [Order proje ayarı](../NewSample/DomainStarters/Order/Sample.DomainStarters.Order.csproj) ASP.NET Core statik varlık manifestini devre dışı bırakır, bu sayede kaynak dizininde fiziksel `wwwroot` gerekmez.

[Order servisi](../NewSample/DomainStarters/Order/Services/OrderService.cs) başarılı kayıt ve iptal işlemlerinde sipariş kimliğini loglar. Kayıt logunda kalem sayısı da bulunur. [Grafana](http://localhost:4300) üzerinde **Explore → Loki → Code** bölümünde aşağıdaki LogQL sorgusunu çalıştırın. Belirli bir siparişi bulmak için sorguya `|= "sipariş-kimliği"` ekleyin.

```logql
{service_name="newsample-order"}
```

Collector adresi [`appsettings.json`](../NewSample/DomainStarters/Order/appsettings.json) içindeki `OpenTelemetry:OtlpEndpoint` ayarından okunur.

### NewSample Payment

[Payment veritabanı betiği](../NewSample/infrastructure/postgres/init-payment.sql) yeni volume'da otomatik çalışır. Mevcut bir volume ile depo kökünden şu komutları kullanın.

```powershell
Get-Content NewSample/infrastructure/postgres/init-payment.sql -Raw | docker compose -f NewSample/compose.yaml exec -T postgres psql -U postgres -d postgres
dotnet run --project NewSample/Participants/Payment
```

Payment, [yapılandırmadaki](../NewSample/Participants/Payment/appsettings.json) `payment` kuyruğundan `AuthorizePayment` ve `RefundPayment` alır. Sonuç, izin verilen `ReplyTo` kuyruğuna gönderilir. Order isteğine opsiyonel `"failStep": "payment"` eklendiğinde yetkilendirme hatası simüle edilir ve ödeme kaydı oluşmaz. Yetkilendirmede `Payment.payment.authorizations` tablosuna sipariş kimliğiyle bir satır yazılır. İade aynı satırı `Cancelled` durumuna geçirir. [Üç kayıt arasındaki doğrulama](01-PROJECT_STATUS.md#newsample-iskeleti).

### NewSample Inventory

[Inventory veritabanı betiği](../NewSample/infrastructure/postgres/init-inventory.sql) yeni volume'da otomatik çalışır. Mevcut volume ile depo kökünden şu komutları kullanın.

```powershell
Get-Content NewSample/infrastructure/postgres/init-inventory.sql -Raw | docker compose -f NewSample/compose.yaml exec -T postgres psql -U postgres -d postgres
dotnet run --project NewSample/Participants/Inventory
```

Temiz veritabanında `sku-1` ve `sku-2` için 100'er, `sku-bulk` için 2000, `sku-limited` için 5 adet örnek stok bulunur. Betik tekrar çalıştırıldığında mevcut stokları korur.

[Inventory ayarları](../NewSample/Participants/Inventory/appsettings.json) ile worker `inventory` kuyruğundan `ReserveInventory` ve `ReleaseInventory` alır. Başarılı rezervasyonda aynı ürüne ait kalemler toplanır ve bütün stok hareketleri tek işlemde kaydedilir. Stok yetersizliği veya `failStep: "inventory"` durumunda Payment iade edilir ve Order iptal olur. Rezervasyon sonrasındaki hatada akış önce stoğu geri ekler, sonra Payment ve Order'ı iptal eder. Rezervasyon kayıtları `Reserved` veya `Released` olarak saklanır. Tekrar teslim stok miktarını ikinci kez değiştirmez.

## S8 — Heterogeneous Technology Participation

`Implemented` — Python Notification, mevcut dört Notification kuyruğunu Pika ile tüketen bağımsız bir süreçtir. Business JSON'dan sonucu üretir. AMQP `CorrelationId`, `sf-version` ve `sf-ex-*` değerlerini korur. Outcome için yeni `MessageId`, `Type`, `sf-outcome` ve Command kimliğini taşıyan `sf-causation-id` üretir. Command'ın `ReplyTo` adresine mandatory yayın yapar ve publisher confirm sonrasında Command'ı ACK'ler. [.NET referans sözleşmesi](../samples/Participants/Shared/ParticipantProtocol.cs), [Python worker](../samples/Participants/SagaFlow.Notification.Python/notification.py).

Mevcut broker ve PostgreSQL ortamında Order, Payment, Inventory ve Shipment'ı çalıştırın. .NET Notification kapalıyken repo kökünden ayrı bir PowerShell konsolunda Python worker'ı başlatın:

```powershell
python -m pip install -r samples/Participants/SagaFlow.Notification.Python/requirements.txt
$env:SAGAFLOW_NOTIFICATION_AMQP_URL = (Get-Content samples/OrderProcess/SagaFlow.Order/appsettings.json -Raw | ConvertFrom-Json).ConnectionStrings.RabbitMq
python samples/Participants/SagaFlow.Notification.Python/notification.py
```

2026-11-10 doğrulamasında `POST /orders` HTTP 201 döndürdü. Dashboard ve `sagaflow_order` PostgreSQL kaydı `b06bbeada7ab40219622b888e027aa8b` için `Completed` gösterdi. Dört adım tamamlandı. Python logu `SendOrderNotificationCompleted` için `order.outcomes` confirm'ini gösterdi. PostgreSQL execution, Command ve correlation kimlikleri logla eşleşti ve Notification sonucu yalnız `orderId`, `recipient`, `message` alanlarından oluştu. Pika encode/decode ile dört business komutu ve iki hata senaryosu 6/6 geçti. Order ve üç .NET worker derlemesi 0 uyarı, 0 hatayla geçti. İlk Dashboard sorgusu tireli GUID ile 404 döndürdü. Tireler kaldırılınca aynı Saga okundu. Manager ve Return dalları ile bağlantı kurtarma gerçek broker akışında sınanmadı. Kanıt dosyaları `.artifacts/s8-order/python-notification.log`, `.artifacts/s8-order/saga-detail.json` ve `.artifacts/s8-order/postgresql-evidence.json`.
