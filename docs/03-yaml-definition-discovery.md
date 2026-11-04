# YAML definitions and Big-Picture discovery

> Bu belgenin sonundaki kapsam ve test sayıları tanım keşfi geliştirme aşamasının tarihsel kaydıdır. Güncel uygulama ve doğrulama durumu: [01-PROJECT_STATUS.md](01-PROJECT_STATUS.md).

SagaFlow now reads and validates declarative definitions and discovers a static cross-domain definition graph. Each Domain Starter owns its local definitions. Discovery reads published snapshots; it never starts a Saga or invokes the coordinator. gRPC carries definitions only; future command/outcome messaging remains separate.

## Güncel process ilişkisi uzantısı (2026-11-03)

Çözülmüş BigPicture düğümüne tıklamak, aynı Dashboard içinde ilgili yerel veya keşfedilmiş uzak workflow diyagramını salt okunur açar; **Back to BigPicture** ilişki grafiğine döner. Çözülememiş düğüm keşif nedenini Drawer'da gösterir. Graph API çözülen düğümlerin workflow adımlarını ve geçişlerini gönderir, `endpoint` adreslerini göndermez. Uzak workflow yalnız karşı publisher'ın yayımladığı tanım kadar görünür. Bu yol [Hosting gRPC/DTO testi](../tests/SagaFlow.Hosting.Tests/DefinitionDiscoveryIntegrationTests.cs), [istemci tıklama testi](../src/SagaFlow.Dashboard.Client/tests/bigPictureGraph.test.tsx) ve gerçek tarayıcıda Order/Return diyagramlarıyla doğrulandı.

BigPicture düğümü açıldığında Dashboard URL'si `/sagaflow/bigpicture/workflow/{domain}/{process}/{version}` olur. Tarayıcı Geri/İleri geçmişi ve **Back to BigPicture** bağlantısı ilişki görünümüne döner; doğrudan Workflow URL'si doğru tanımı yükler. Remote tanım yalnız Discovery grafiğinden gelir ve yerel `Workflows` listesine eklenmez. Tanım bulunamazsa grafik üretilmez, açıklama gösterilir. Gömülü Dashboard, yapılandırılmış kök yolunda derin URL'nin varlıklarını HTML `base` etiketiyle yükler. [Route kodu](../src/SagaFlow.Dashboard.Client/src/layout/bigPictureRoute.ts), [Hosting asset testi](../tests/SagaFlow.Hosting.Tests/LocalSagaCoordinationHostingTests.cs) ve [navigasyon testleri](../src/SagaFlow.Dashboard.Client/tests/workflowNavigation.test.tsx) bu yolu sınar.

`bigPicture` kaydına isteğe bağlı `relationship: related` eklendi. `related`, kaynak Domain Starter'ın hedef süreçle **tanım düzeyinde iş ilişkisi** bildirmesidir. Hedef domain'in karşılıklı onayı, süreç çağrısı, parent/child yürütümü, ortak Saga instance, runtime correlation veya merkezi koordinasyon anlamına gelmez. Alanı olmayan eski YAML aynı biçimde yüklenir ve grafikte etiketsiz kenar oluşturur. Açık boş, `null` veya `related` dışındaki değerler [`ProcessDefinitionValidator`](../src/SagaFlow.Engine/ProcessDefinition/ProcessDefinitionValidator.cs) tarafından reddedilir; kaynak model [`BigPictureSourceDefinition`](../src/SagaFlow.Core/Definitions/BigPictureSourceDefinition.cs) ve snapshot [`DefinitionSnapshot`](../src/SagaFlow.Engine/ProcessDefinition/DefinitionSnapshot.cs) alanın yokluğunu korur.

Order örneği [`order-process.yaml`](../samples/OrderProcess/SagaFlow.Order/saga/order-process.yaml) içinde `return/return-process@1.0` için `relationship: related` bildirir. Adres, Return'un mevcut HTTPS launch profilindeki `https://localhost:65305` değeridir; Order [`appsettings.json`](../samples/OrderProcess/SagaFlow.Order/appsettings.json) bu origin'i keşif allowlist'ine alır. Normal Return geliştirme profili yalnız `return/return-process@1.0` tanımının gRPC yayınını açar; profil dışı çalıştırmada yayın varsayılan olarak kapalı kalır. Yerel demo için iki ayrı terminalde:

```powershell
docker compose -f samples/docker-compose.yml up -d
dotnet dev-certs https --trust
```

```powershell
Set-Location samples/ReturnProcess/SagaFlow.Return
dotnet run --launch-profile SagaFlow.Return
```

```powershell
Set-Location samples/OrderProcess/SagaFlow.Order
dotnet run --launch-profile SagaFlow.Order
```

`https://localhost:50823/sagaflow/` sayfasını açın. Normal Order profili uzak gRPC tanım yayını açmaz; buna rağmen yerel `api/definitions` yanıtındaki `relationship: related` ayrı **BigPicture** menüsünü gösterir. Giriş gerekmez. `Workflows` yalnız yerel workflow grafiğini gösterir. BigPicture sayfası Order **Local · Resolved** → Return **Remote · Resolved** kenarını `related` etiketiyle ve sürümleri düğümlerde gösterir; düğüm/kenar seçimi sağ Drawer'ı açar. Graph API, mevcut allowlist ve gRPC keşif kurallarıyla `GET /sagaflow/api/definition-graph?domain=order&process=order-process&version=1.0` çağrısını yapar; Dashboard API yanıtları endpoint adresini içermez. Return yayını kapalı veya erişilemiyorsa ilişki korunur, hedef `Remote · Unresolved` ve keşif nedeni gösterilir. Legacy `bigPicture` referansı tek başına menüyü açmaz. Return kendi Saga'sı ve state'i üzerinde bağımsız kalır. [BigPicture sayfası](../src/SagaFlow.Dashboard.Client/src/pages/BigPicturePage.tsx), [grafik eşlemesi](../src/SagaFlow.Dashboard.Client/src/components/BigPicture/bigPictureMapping.ts) ve [navigasyon testleri](../src/SagaFlow.Dashboard.Client/tests/workflowNavigation.test.tsx) bu davranışı gösterir.

BigPicture açıkken istemci mevcut Dashboard graph API yanıtını varsayılan **15 saniyede bir** yeniler; ayrı bir server hosted service veya SignalR topoloji olayı yoktur. Graph API her çağrıda mevcut gRPC Definition Discovery ve allowlist/timeout kurallarını kullanır. Tek zamanlayıcıyla üst üste binmeyen yenileme yapılır; gizli sekmede istek ertelenir, sayfadan ayrılınca istek iptal edilir. Uzak yayıncı açılınca hedef Resolved, kapanınca Unresolved, aynı adreste yeniden açılınca Resolved olur; bildirim için browser yenilemesi gerekmez. HTTP 401/403 mevcut grafiği temizler; geçici Dashboard API hatası uzak hedefin başarısızlığı gibi yorumlanmaz. Gerçek ayrı Order/Return süreçleriyle graph API durum geçişleri [Hosting testinde](../tests/SagaFlow.Hosting.Tests/SampleOrderReturnDiscoveryTests.cs), zamanlayıcı ve Drawer davranışı [istemci testlerinde](../src/SagaFlow.Dashboard.Client/tests/bigPicturePage.test.tsx) sınandı; gerçek tarayıcıda Order açıkken Return kapalı → açık → kapalı → yeniden açık ve Return önce → Order sonra geçişleri manuel yenilemesiz doğrulandı. Başlatma sırası önemli değildir: Order açıkken Return sonradan erişilebilir olursa sonraki yenilemede keşfedilir.
Çoklu yerel tanım grafikleri birleştirilirken `(domain, process, version)` kimliği yerel Dashboard tanım listesinden alınır; aynı tanımın uzak keşif kopyası Local sahipliğini değiştiremez. Keşif hatası API yanıtı isteğe bağlı `requestedVersion` taşır; istemci hata nedenini kaynak kimliği, hedef kimliği ve istenen sürümle eşleştirir. Eski, sürüm alanı olmayan yanıtlarda neden yalnız eşleşme tek anlamlıysa gösterilir; belirsiz neden başka bir sürüme atanmaz. [Birleştirme testleri](../src/SagaFlow.Dashboard.Client/tests/bigPicturePage.test.tsx), [neden eşleme testleri](../src/SagaFlow.Dashboard.Client/tests/bigPictureGraph.test.tsx), [HTTP DTO testi](../tests/SagaFlow.Hosting.Tests/DefinitionDiscoveryIntegrationTests.cs).

`DefinitionYamlCodec` açık ilişkiyi canonical YAML ve gRPC yayını boyunca korur. **Yerel Dashboard topolojisi** yerel tanımlar için kimlik doğrulama istemeden tanım ve graph API yanıtlarında görünür; endpoint adresleri yanıtlardan çıkarılır. Dashboard erişimi ağ veya host düzeyinde sınırlandırılmalıdır; Dashboard'a erişebilen herkes yerel ilişki metadata'sını okuyabilir. Otomatik middleware ve `MapSagaFlowDashboard` aynı davranışı uygular. **Uzak gRPC yayını** ayrıca `Publication.Enabled`, yerel tanım yayın allowlist'i ve topoloji için `IncludeBigPictureSources=true` gerektirir; varsayılan kapalıdır. Dashboard görünürlüğü bu koşulları değiştirmez. gRPC endpoint'i host'un kendi gRPC yetkilendirme yapılandırmasıyla, uzak keşif ise endpoint allowlist'iyle yönetilir. Yerel grafiğin uzak tanımı bulması yürütüm erişilebilirliğini veya herhangi bir Saga'nın başladığını kanıtlamaz. [Gerçek HTTP/gRPC testleri](../tests/SagaFlow.Hosting.Tests/DefinitionDiscoveryIntegrationTests.cs), [ayrı örnek process testi](../tests/SagaFlow.Hosting.Tests/SampleOrderReturnDiscoveryTests.cs) ve [Dashboard testleri](../src/SagaFlow.Dashboard.Client/tests/bigPictureGraph.test.tsx) bu kapsamı sınar.

## Model and YAML contract

Structured `when` comparisons, `on` aliases, context/step-result paths, and the Order
shipping/pickup example are described in [conditional transitions](05-conditional-transitions.md).
The Boolean expression rules below describe the preserved legacy scalar format.

`SagaDefinition` has `Name`, `Version`, `Domain`, `Steps`, and `BigPicture`. Steps have `Name`, `Command`, optional `TimeoutLimit`, `MaxAttempts` (default `1`), `Transitions`, and optional `Compensation`. Transitions have `Event`, optional `When`, optional `Else`, and `NextStep`. `BigPictureSourceDefinition` contains `Domain`, `Process`, `Endpoint`, and an optional `Version` pin.

```yaml
name: order-process
version: "1.0"
domain: order
steps:
  - name: payment
    command: AuthorizePayment
    timeoutLimit: 10s
    maxAttempts: 3
    compensation:
      command: RefundPayment
    transitions:
      - event: success
        nextStep: inventory
      - event: failure
        nextStep: compensation
      - event: timeout
        nextStep: compensation
  - name: inventory
    command: ReserveInventory
    timeoutLimit: 20s
    maxAttempts: 2
    compensation:
      command: ReleaseInventory
    transitions:
      - event: success
        when: "result.available == true"
        nextStep: shipment
      - event: success
        else: true
        nextStep: compensation
      - event: failure
        nextStep: compensation
      - event: timeout
        nextStep: compensation
  - name: shipment
    command: CreateShipment
    transitions:
      - event: success
        nextStep: completed
      - event: failure
        nextStep: compensation
bigPicture:
  - domain: payment
    process: payment-process
    endpoint: "https://payment-service:7001"
  - domain: fulfillment
    process: fulfillment-process
    endpoint: "https://fulfillment-service:7002"
    # Optional: version: "1.0"
```

Identifiers use ASCII letters, digits, underscore and hyphen, start with a letter, and have at most 128 characters. Identity is the exact, case-sensitive tuple `(domain, name, version)`. Versions use numeric `major.minor` or `major.minor.patch`, without leading zeroes. `1.0` and `1.0.0` are distinct identifiers; there is no implicit latest-version selection.

`trigger` and `initialStep` are no longer part of the contract. A nonempty `steps` list is required and its first element is the automatic start step. Later execution order is selected only by `transitions.nextStep`.

Step names must be unique. `completed` and `compensation` are reserved targets and cannot be step names. Every step needs a command and at least one transition. Events are exactly `success`, `failure`, or `timeout`; `when` is an additional predicate on that outcome. Targets must be a local step or a reserved target. Individual compensation definitions are optional; when supplied, their command must be valid. `timeoutLimit` accepts positive integer durations with `ms`, `s`, `m`, or `h` units and drives Engine-side timeout detection after dispatch. `maxAttempts` remains definition metadata only; retry execution and automatic timeout-transition execution are not implemented.

For each event category, at most one fallback/default is allowed. `else: true` is an explicit fallback and cannot contain `when`; `else: false` is rejected. A single unconditional transition is also a default and may accompany conditional rules. Conditions take precedence over the fallback independently of declaration order. Conditions on the same Boolean property must require opposite values. Equality and inequality normalize logically: `result.available == true` is equivalent to `result.available != false` and cannot coexist as a separate branch. Conditions on different properties are rejected together because they could both match. A single condition requires a fallback; an exhaustive pair of complementary Boolean conditions supplies complete Boolean coverage without an extra fallback. These are static validation rules, not a runtime evaluator; no outcome category is inferred if it has no rules.

Graph validation starts at the first listed step: every declared step must be reachable, and every reachable step must have a structural path to `completed` or `compensation`. Active-path DFS rejects reachable executable self-loops and multi-step cycles with their participant names, even when a terminal exit exists: each executable step may run only once per Saga instance. Completed DFS nodes may be shared by converging DAG branches without forming a cycle. A fallback behind exhaustive complementary conditions does not count as a viable edge. A one-step process that transitions directly to a terminal is valid; an empty process is not. Validation does not evaluate property values or correlate conditions across multiple steps.

### Safe condition syntax

```text
condition := propertyPath ("==" | "!=") ("true" | "false")
propertyPath := identifier ("." identifier)+
identifier := [A-Za-z_][A-Za-z0-9_]*
```

Whitespace around operators is allowed. The syntax is case-sensitive and limited to 512 characters. `ConditionParser.Parse` returns `BooleanCondition(PropertyPath, IsEquality, ExpectedValue)`, a reusable syntax representation. The definition validator separately requires the `result.` root, matching the runtime contract; `data.*`, `context.*` and rootless conditions are rejected during YAML loading. Nested Boolean properties and both `==` and `!=` remain supported. No expression is evaluated during validation. Calls, indexing, numeric/string/null literals, logical operators, assignment, reflection and scripting are unsupported and rejected.

### Loading and validation

`LoadFromDirectory("saga")` loads top-level `.yaml` and `.yml` files in ordinal filename order during host startup. Paths are resolved relative to the host content root. Loading is all-or-nothing before registry registration; an invalid file or duplicate identity/version aborts startup. Multiple versions of a process are allowed. Duplicate identities are rejected even when content is identical. No watchers or remote calls run during local loading.

`LoadFromYaml("saga/order.yaml")` remains available for one file. Choose one source mode. The direct `YamlSagaDefinitionLoader(sourceDirectory)` API confines file lookups to that directory. Symlinks/reparse points are rejected to avoid traversing outside the configured source. Unknown YAML properties, duplicate keys, additional documents, malformed content, explicit null collections, and invalid model fields are rejected with source and process/step context. Input is limited to 1 MiB, 256 steps, 128 transitions per step, and 128 Big-Picture references per definition.

Hosted startup resolves `ISagaDefinitionLoader` from DI and calls its `LoadAsync` for each configured file, including directory mode. Register a replacement/decorator before or after `AddSagaFlow`; the default loader uses `TryAddSingleton` so earlier registrations are preserved. Directory enumeration retains the shared filename ordering and path policy. The registry validates and atomically stores the loaded batch; a failed load cannot partially register local definitions. No additional loader abstraction is introduced.

`DefinitionYamlCodec` also validates canonical YAML received remotely. `ProcessDefinitionRegistry` stores defensive deep snapshots with read-only collections and an explicit `Local`/`Remote` origin. `Find(ProcessDefinitionId)` resolves an exact identity. Registration is atomic; remote snapshots cannot replace an existing snapshot or local definition. Explicit local registration can supersede a remote snapshot of the same identity. `Snapshot()` provides an ordered stable view. Discovery itself keeps remote results in its returned graph; it does not mutate the host's local registry or make remote definitions executable.

## Host integration

```csharp
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Hosting.Endpoints;

builder.Services.AddSagaFlow(options =>
{
    options.LoadFromDirectory("saga");
    options.Publication.Enabled = true;
    options.Publication.PublishedDefinitions.Add(new("order", "order-process", "1.0"));

    // Opt in only if these topology addresses are safe to publish.
    options.Publication.IncludeBigPictureSources = true;

    options.Discovery.AllowedEndpoints.Add("https://payment-service:7001");
    options.Discovery.AllowedEndpoints.Add("https://fulfillment-service:7002");
    options.Discovery.RequestTimeout = TimeSpan.FromSeconds(3);
    options.Discovery.OverallTimeout = TimeSpan.FromSeconds(15);
    options.Discovery.MaximumDepth = 4;
    options.Discovery.MaximumRequests = 32;
});

var app = builder.Build();
app.MapSagaFlowDefinitionDiscovery();
// Alternatively: app.MapSagaFlowDefinitionDiscovery().RequireAuthorization("definitions");
app.Run();
```

Registration follows ASP.NET Core's `AddGrpc`/`MapGrpcService` conventions internally. No domain-specific gRPC implementation is needed. Publication is disabled by default. Enabling it requires a nonempty exact identity allowlist and endpoint mapping; every published identity must be loaded locally or startup fails. Mapping while disabled or mapping twice fails clearly. A host with publication disabled can still load YAML and use discovery.

The host configures ordinary Kestrel HTTP/2 and TLS, certificates, and any authentication/authorization policy. The client uses HTTPS with normal system certificate verification. HTTP is rejected unless the host explicitly sets `Discovery.AllowInsecureHttp = true` and allowlists the exact HTTP origin, intended for local fixtures only. No certificate bypass, secret, or runtime command endpoint is added.

## Protobuf service

Source: `src/SagaFlow.Hosting/Protos/definitions_v1.proto`.

Package `sagaflow.definitions.v1`, service `ProcessDefinitions`:

- `ListDefinitions(ListDefinitionsRequest)` returns protocol version 1 and published local `(domain, process, version)` identities.
- `GetDefinition(GetDefinitionRequest)` returns protocol version 1, an identity envelope, and canonical YAML.
- An omitted request version succeeds only if exactly one published matching version exists. Multiple versions return `FailedPrecondition`; callers should pin a version. Missing/unpublished identities return `NotFound`; malformed identity requests return `InvalidArgument`.

The server never returns file paths or raw source text. Canonical output contains only the supported public model. `bigPicture` references (including endpoint addresses) are omitted by default. Recursive traversal through a remote process requires that publisher to explicitly enable `IncludeBigPictureSources`; otherwise discovery ends at that process. Publication allowlists are independent of registry contents: remote entries and private local entries are excluded.

## Discovery and graph use

```csharp
using SagaFlow.Engine.Discovery;
using SagaFlow.Engine.ProcessDefinition;

var registry = app.Services.GetRequiredService<ProcessDefinitionRegistry>();
var local = registry.Find(new("order", "order-process", "1.0"))!;
var discovery = app.Services.GetRequiredService<DefinitionDiscovery>();
var graph = await discovery.DiscoverAsync(local.Definition, cancellationToken);
// graph.Nodes: identity, immutable definition, Local/Remote origin.
// graph.Edges: source, requested identity/version, endpoint, resolved target if known.
// graph.Failures: source, requested target, categorized reason.
```

Each call builds a new graph and leaves local definitions intact. Responses must match the request, protocol version, envelope and YAML identity. Invalid definitions are rejected. A failure on one branch does not remove successful siblings. Caller cancellation propagates; request deadlines become structured `Timeout` failures. Other categories include `UntrustedEndpoint`, `NotFound`, `AmbiguousVersion`, `Unavailable`, `InvalidResponse`, `Cycle`, and `LimitExceeded`. On some platforms a stopped endpoint may time out before its socket reports it unavailable; both outcomes are bounded.

Discovery uses deterministic depth-first traversal, ancestor cycle checks, successful-response reuse per endpoint/request identity, a request limit, and a depth limit (root depth 0). The defaults are 4 levels and 32 requests. Configuration is limited to depth 1..16, requests 1..256, and per-request timeout at most one minute. Previously discovered identities are not recursively revisited; conflicting content for the same identity/version is rejected.

`OverallTimeout` bounds the entire discovery operation (default 15 seconds, configurable from greater than zero up to five minutes). A linked operation deadline cancels an active request even when its per-request timeout is longer. Traversal stops globally after budget exhaustion, preserving already validated definitions and edges and returning a structured `OverallTimeout` failure for the interrupted/pending reference. Remaining references are not requested and their edges may be absent from the partial result. A per-request `Timeout` alone permits traversal to continue while overall time remains. Caller cancellation during an active call still propagates as `OperationCanceledException`. Discovery never updates the local registry. The sequential implementation retains its depth/request/cycle limits; synchronous parsing is bounded by definition-size limits and checks the deadline before accepting a response.

Endpoints are absolute HTTP(S) origins with no credentials, query, fragment, or non-root path. Exact origin allowlisting is deny-by-default and applies to every transitive reference. Redirects and automatic proxy fallback are disabled. Trusting an origin includes trusting its DNS resolution and ownership; choose host-controlled endpoints. YAML and discovery graph contents are configuration, not untrusted request parameters. Protect graph output if it contains private topology. Direct `GrpcRemoteDefinitionClient` is the transport primitive; use `DefinitionDiscovery` for the endpoint policy and response validation.

## Executable A/B/C demonstration

The test fixtures are lightweight real Kestrel hosts using ephemeral loopback ports and HTTP/2. They represent three Domain Starters for definition discovery, not the thesis's five-service runtime prototype.

```powershell
dotnet test .\tests\SagaFlow.Hosting.Tests\SagaFlow.Hosting.Tests.csproj --filter FullyQualifiedName~ThreeDomainsLoadTheirYamlAndDiscoverDefinitionGraphOverRealGrpc
```

The test writes B's payment YAML and C's fulfillment YAML to their own temporary directories, starts both publishers, writes A's order YAML with B/C references, then starts A. A discovers three nodes and two edges; B/C retain local ownership. The fixtures remove coordinator registration, proving this path works without any coordinator instance. Hosts and temporary directories are disposed after the test.

### Separate OS-process verification (R-01)

```powershell
dotnet test .\tests\SagaFlow.Hosting.Tests\SagaFlow.Hosting.Tests.csproj --no-restore --filter FullyQualifiedName~CrossProcessDefinitionDiscoveryTests
```

The hosting test project builds and copies `SagaFlow.Order` as a process fixture. The test launches it through the .NET host in a separate OS process with an OS-assigned loopback port and an explicit HTTP/2 listener. It reads its readiness address from redirected output with a 15-second startup deadline. The test runner is the client process; its PID differs from the publisher PID. It retrieves actual YAML through gRPC, validates the identity/version/domain and first step, and checks that the returned `bigPicture` entry points to another lightweight Domain Starter. It then discovers a three-node/two-edge graph through the child publisher while local ownership remains intact.

The fixture kills and waits for the child process tree in `DisposeAsync`, including startup failure and assertion-failure paths; process exit and output draining each have a five-second bound. Temporary files are removed after shutdown. The test uses plain loopback HTTP/2, not development TLS: it does not verify certificate trust, authentication, production networking, or production service discovery. It is definition-discovery evidence only, not runtime Saga evidence.

Bu paragraf ilk tanım keşfi PoC'sinin tarihsel sample durumudur: o aşamada `/demo/purchase-definition` ve `/demo/definition-graph` örnek uçları, `bigPicture: []` ve Dashboard değişikliği olmaması geçerliydi. Güncel Order → Return ilişkisi, demo adresi ve Dashboard grafiği için bu belgenin başındaki **Güncel process ilişkisi uzantısı** bölümüne bakın.

The sample accepts the host-controlled `SagaFlow:DefinitionDirectory` setting (default `saga`) and `SagaFlow:PublishBigPictureSources` (default false). These permit the cross-process fixture to load a temporary definition directory and explicitly publish its safe topology; neither can be supplied through an RPC or HTTP query.

## Dependencies and verification

Added: YamlDotNet 18.1.0 (Engine), Google.Protobuf 3.36.2, Grpc.AspNetCore.Server 2.84.0, Grpc.Net.Client 2.84.0, and private build-time Grpc.Tools 2.84.0 (Hosting). Generated Protobuf C# stays under `obj`; the `.proto` is the source contract.

```powershell
dotnet build .\SagaFlow.slnx
dotnet test .\SagaFlow.slnx --no-restore --logger "console;verbosity=minimal"
```

Tests cover parser/validator failures, multiple files and versions, condition grammar, immutable registry ownership, automatic publication, real gRPC retrieval, invalid/mismatched responses, missing and ambiguous versions, endpoint policy, failures and deadlines, cycles/limits, caller cancellation, and hosts with gRPC disabled. Existing smoke tests remain.

The original implementation baseline had 57 passing tests. After all six review corrections, the required `dotnet build .\SagaFlow.slnx --no-restore` succeeded with zero warnings/errors and `dotnet test .\SagaFlow.slnx --no-build --no-restore` passed 98 tests (Core 1, Engine 73, Hosting 24), with zero failures/skips. The 41 added cases include separate-OS-process HTTP/2 gRPC verification. Deployment TLS and authentication configuration were not exercised.

### Review-fix verification commands

Each correction was tested before moving to the next finding:

```powershell
dotnet test .\tests\SagaFlow.Engine.Tests\SagaFlow.Engine.Tests.csproj --no-restore --filter FullyQualifiedName~DefinitionStartTests
dotnet test .\tests\SagaFlow.Hosting.Tests\SagaFlow.Hosting.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~ThreeDomainsLoadTheirYamlAndDiscoverDefinitionGraphOverRealGrpc|FullyQualifiedName~PublicationListsOnlyAllowedLocalDefinitionsAndRedactsTopologyByDefault"
dotnet test .\tests\SagaFlow.Engine.Tests\SagaFlow.Engine.Tests.csproj --no-restore --filter FullyQualifiedName~ConditionalTransitionTests
dotnet test .\tests\SagaFlow.Engine.Tests\SagaFlow.Engine.Tests.csproj --no-restore --filter FullyQualifiedName~DefinitionGraphValidationTests
dotnet test .\tests\SagaFlow.Engine.Tests\SagaFlow.Engine.Tests.csproj --no-restore --filter FullyQualifiedName~DiscoveryDeadlineTests
dotnet test .\tests\SagaFlow.Hosting.Tests\SagaFlow.Hosting.Tests.csproj --no-restore --filter FullyQualifiedName~OverallBudgetCancelsRealGrpcRequestAndPreservesEarlierSuccess
dotnet test .\tests\SagaFlow.Hosting.Tests\SagaFlow.Hosting.Tests.csproj --no-restore --filter FullyQualifiedName~DefinitionLoaderRegistrationTests
dotnet test .\tests\SagaFlow.Hosting.Tests\SagaFlow.Hosting.Tests.csproj --no-restore --filter FullyQualifiedName~CrossProcessDefinitionDiscoveryTests
dotnet build .\SagaFlow.slnx --no-restore
dotnet test .\SagaFlow.slnx --no-build --no-restore
```

Regression suites cover first-step startup, branches, graph viability, deadlines, DI loading and separate-process retrieval. The Hosting test project references the sample only to build/copy its executable fixture.

## Changed-file inventory

Modified:

```text
README.md
samples/OrderProcess/SagaFlow.Order/Program.cs
samples/OrderProcess/SagaFlow.Order/saga/order-process.yaml
src/SagaFlow.Core/Definitions/SagaDefinition.cs
src/SagaFlow.Core/Definitions/SagaStepDefinition.cs
src/SagaFlow.Core/Definitions/SagaTransitionDefinition.cs
src/SagaFlow.Engine/ProcessDefinition/ProcessDefinitionValidator.cs
src/SagaFlow.Engine/ProcessDefinition/YamlSagaDefinitionLoader.cs
src/SagaFlow.Engine/README.md
src/SagaFlow.Engine/SagaFlow.Engine.csproj
src/SagaFlow.Hosting/DependencyInjection/SagaFlowServiceCollectionExtensions.cs
src/SagaFlow.Hosting/README.md
src/SagaFlow.Hosting/SagaFlow.Hosting.csproj
tests/SagaFlow.Hosting.Tests/SagaFlow.Hosting.Tests.csproj
```

Added:

```text
docs/03-yaml-definition-discovery.md
src/SagaFlow.Core/Definitions/BigPictureSourceDefinition.cs
src/SagaFlow.Engine/Discovery/DefinitionDiscovery.cs
src/SagaFlow.Engine/ProcessDefinition/ConditionParser.cs
src/SagaFlow.Engine/ProcessDefinition/DefinitionSnapshot.cs
src/SagaFlow.Engine/ProcessDefinition/DefinitionValidationException.cs
src/SagaFlow.Engine/ProcessDefinition/DefinitionYamlCodec.cs
src/SagaFlow.Engine/ProcessDefinition/ProcessDefinitionRegistry.cs
src/SagaFlow.Hosting/Definitions/DefinitionLoadingService.cs
src/SagaFlow.Hosting/Definitions/DefinitionPublicationOptions.cs
src/SagaFlow.Hosting/Definitions/GrpcRemoteDefinitionClient.cs
src/SagaFlow.Hosting/Definitions/ProcessDefinitionsGrpcService.cs
src/SagaFlow.Hosting/Endpoints/SagaFlowDefinitionEndpoints.cs
src/SagaFlow.Hosting/Protos/definitions_v1.proto
tests/SagaFlow.Engine.Tests/ProcessDefinitionTests.cs
tests/SagaFlow.Hosting.Tests/DefinitionDiscoveryIntegrationTests.cs
tests/SagaFlow.Engine.Tests/TriggerInitialStepTests.cs
tests/SagaFlow.Engine.Tests/ConditionalTransitionTests.cs
tests/SagaFlow.Engine.Tests/DefinitionGraphValidationTests.cs
tests/SagaFlow.Engine.Tests/DiscoveryDeadlineTests.cs
tests/SagaFlow.Hosting.Tests/DefinitionLoaderRegistrationTests.cs
tests/SagaFlow.Hosting.Tests/CrossProcessDefinitionDiscoveryTests.cs
```

## Scope and thesis alignment

This implements the structural Declarative Process Definition foundation and a static Big-Picture discovery capability. Domain ownership and in-process placement follow the Domain Starter component responsibilities. gRPC discovery is an optional embedded capability, not a new architectural container. Extending references with an optional version pin is an explicit deterministic lookup policy, not an execution dependency.

This document describes the definition/discovery feature, which never executes a process. A subsequent [local coordination PoC](04-local-saga-coordination.md) adds explicit `StartAsync`, forward command/outcome handling, Boolean condition evaluation, volatile instance state, and compensation planning through separate runtime classes. Broker delivery, durable state, compensation execution, restart recovery, durable idempotency, runtime traces, a dashboard graph, authentication provisioning, watchers/hot reload, and the five-service representative prototype remain outside the implementation. Static discovery remains distinct from runtime execution observability.

## S2-1 invocation contracts and review resolution

Contract/context foundation: `Implemented`; cross-domain runtime: `Planned`; S2 remains `Partial`.
[`ProcessDefinitionValidator`](../src/SagaFlow.Engine/ProcessDefinition/ProcessDefinitionValidator.cs) now accepts `related` and `invokes`; legacy omission is preserved. Only explicit `invokes` with an exact version authorizes target resolution. Discovery endpoint is never a runtime routing address. Canonical YAML/snapshot and real gRPC preservation are covered by [`CrossProcessDefinitionDiscoveryTests`](../tests/SagaFlow.Hosting.Tests/CrossProcessDefinitionDiscoveryTests.cs).

[`SagaStepExecutor`](../src/SagaFlow.Engine/Coordination/SagaStepExecutor.cs) runs optional [`ILocalSagaStepHandler`](../src/SagaFlow.Engine/Coordination/LocalSagaExecution.cs) handlers before forward command publication. Register a scoped handler in the Domain Starter, inject ISagaFlow, then call InvokeAsync(target, payload) from ExecuteAsync. Participants need no source execution context. [`LocalSagaExecutionBoundary`](../src/SagaFlow.Hosting/DependencyInjection/LocalSagaExecutionBoundary.cs) creates a fresh async DI scope per command, binds the engine instance/step, and clears it in finally on return, exception or cancellation. No AsyncLocal/static context exists. Calls outside this boundary fail. Escaped ISagaFlow references become invalid when the handler returns; fire-and-forget handler work is unsupported. No registered handlers preserves existing StartAsync dispatch behavior; handler exceptions follow dispatch failure policy.

[`SagaCoordinator`](../src/SagaFlow.Engine/Coordination/SagaCoordinator.cs) validates current step and local ownership in an atomic store update. Saga must be Running and the same step Dispatched. [`SagaStepState.Invocation`](../src/SagaFlow.Core/State/SagaStepState.cs) stores one immutable prepared request per executable step. Repeating identical target/payload retains Invocation ID; changed target/payload fails. Different steps/instances obtain independent records. The in-memory snapshot preserves these records; no durable adapter/recovery was added. Receipt Prepared means local registration only, with no remote acceptance or message publication.

[`ProcessInvocation`](../src/SagaFlow.Core/Messages/ProcessInvocation.cs) validates UUID invocation/message IDs, process identifiers/version, required correlation and Saga IDs. Accepted requires target Saga ID; terminal outcomes allow only Completed/Failed/Compensated. OccurredAt is producer UTC event time; relationship UpdatedAt is local observation time. Target Saga ID may be absent before acceptance. Message ID identifies one logical message and must survive redelivery; Invocation ID identifies the entire interaction. The full source/target/correlation identity allows ValidateAgainst to reject mismatches. S2-2 must implement receiver deduplication and atomically reject conflicting acceptance/terminal outcomes. Request generation is local preparation, not a send.

Evidence: [`InvocationExecutionTests`](../tests/SagaFlow.Hosting.Tests/InvocationExecutionTests.cs), [`InvocationValidationTests`](../tests/SagaFlow.Core.Tests/InvocationValidationTests.cs), [`ProcessInvocationContractTests`](../tests/SagaFlow.Engine.Tests/ProcessInvocationContractTests.cs). Cross-domain publishers/consumers, remote StartAsync, terminal propagation, persistence and Dashboard runtime relationships remain S2-2 or later.
