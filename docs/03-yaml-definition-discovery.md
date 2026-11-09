# YAML definitions and Big-Picture discovery

> Bu belgenin sonundaki kapsam ve test sayıları tanım keşfi geliştirme aşamasının tarihsel kaydıdır. Güncel uygulama ve doğrulama durumu: [01-PROJECT_STATUS.md](01-PROJECT_STATUS.md).

SagaFlow now reads and validates declarative definitions and discovers a static cross-domain definition graph. Each Domain Starter owns its local definitions. Discovery reads published snapshots; it never starts a Saga or invokes the coordinator. gRPC carries definitions only; future command/outcome messaging remains separate.

## Güncel process ilişkisi uzantısı (2026-11-03)

Process Explorer, Dashboard kenar çubuğundaki tek süreç gezinme girişidir. BigPicture topolojisini açar ve ilişkisi olmayan host'ta her yerel tanımı bağımsız **Local** düğüm olarak gösterir. Bir düğüme tıklamak aynı workflow renderer'ında süreç tanımını açar. Düğümün bilgi düğmesi metadata ve keşif nedenini mevcut Drawer'da gösterir. Kenar ve instance ilişki detayları da aynı Drawer'ı kullanır. [Gezinme testleri](../src/SagaFlow.Dashboard.Client/tests/workflowNavigation.test.tsx).

Local workflow, `api/definitions` yanıtındaki tam `(domain, process, version)` eşleşmesinden gelir. Remote workflow, `api/definition-graph` yanıtında gRPC discovery ile çözülmüş ve yayımlanmış `node.definition` alanından gelir. Sürümü veya tanımı bulunmayan hedef açıklamalı bir unavailable görünümü açar. [Hosting gRPC/DTO testi](../tests/SagaFlow.Hosting.Tests/DefinitionDiscoveryIntegrationTests.cs) ve [grafik testleri](../src/SagaFlow.Dashboard.Client/tests/bigPictureGraph.test.tsx).

Topoloji adresi `{dashboardPath}/bigpicture` olarak kalır. Eski `/workflows` adresi de topolojiyi açar. Workflow adresi `/bigpicture/workflow/{domain}/{process}/{version}` biçimindedir. Sürümü belirtilmemiş çözümsüz hedefin adresi sürüm segmenti içermez. Workflow breadcrumb’ının son segmenti seçilen domain adıdır. Breadcrumb’daki **Process Explorer** bağlantısı ve **Back to Topology**, mevcut instance context sorgusunu koruyarak topolojiye döner. Tarayıcı Geri/İleri geçmişi ve doğrudan bağlantılar çalışır. Gömülü Dashboard, yapılandırılmış kök yolunda derin URL'nin varlıklarını HTML `base` etiketiyle yükler. [Route kodu](../src/SagaFlow.Dashboard.Client/src/layout/bigPictureRoute.ts) ve [Hosting asset testi](../tests/SagaFlow.Hosting.Tests/LocalSagaCoordinationHostingTests.cs).

Process Explorer'ın kalıcı mimari sınırı salt okunur gözlem ve görselleştirmedir. Yayımlanan tanımları ve topolojiyi okumak bu kapsamdadır. Workflow başlatma, yürütüm kontrolü, tanım düzenleme, domain yapılandırması ve uzak domain yönetimi mimari kapsamın dışındadır.

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

`https://localhost:50823/sagaflow/` sayfasını açıp **Process Explorer** girişini seçin. Normal Order profili, kendi gRPC tanım yayını kapalıyken de yerel `api/definitions` yanıtındaki `relationship: related` ile topolojiyi sunar. Return yayıncısı erişilebilirse grafik Order **Local · Resolved** → Return **Remote · Resolved** kenarını `related` etiketiyle gösterir. Düğüm workflow'a, bilgi düğmesi veya kenar Drawer'a gider. `GET /sagaflow/api/definition-graph?domain=order&process=order-process&version=1.0` mevcut allowlist ve gRPC keşif kurallarını kullanır. Dashboard DTO'ları endpoint adreslerini dışarıda bırakır. Return yayını erişilemezse ilişki ve `Remote · Unresolved` düğümü görünür, Drawer keşif nedenini gösterir. [Topoloji eşlemesi](../src/SagaFlow.Dashboard.Client/src/components/BigPicture/bigPictureMapping.ts).

Process Explorer açıkken istemci yapılandırılmış ilişkilerin mevcut Dashboard graph API yanıtını varsayılan **15 saniyede bir** yeniler; ayrı bir server hosted service veya SignalR topoloji olayı yoktur. Graph API her çağrıda mevcut gRPC Definition Discovery ve allowlist/timeout kurallarını kullanır. Tek zamanlayıcıyla üst üste binmeyen yenileme yapılır; gizli sekmede istek ertelenir, sayfadan ayrılınca istek iptal edilir. Uzak yayıncı açılınca hedef Resolved, kapanınca Unresolved, aynı adreste yeniden açılınca Resolved olur; bildirim için browser yenilemesi gerekmez. PE-01 ile graph isteklerinin sonuçları root bazında birleştirilir ve hatalar ayrı uyarıda gösterilir. Bir root için HTTP 401/403 geldiğinde o root’un önceki graph verisi kaldırılır. Geçici hatada aynı root’un daha önce başarıyla alınan verisi uyarıyla birlikte görünür kalabilir. Diğer root’ların başarılı sonuçları ve yerel `api/definitions` düğümleri erişilebilir kalır. Gerçek ayrı Order/Return süreçleriyle graph API durum geçişleri [Hosting testinde](../tests/SagaFlow.Hosting.Tests/SampleOrderReturnDiscoveryTests.cs), zamanlayıcı ve Drawer davranışı [istemci testlerinde](../src/SagaFlow.Dashboard.Client/tests/bigPicturePage.test.tsx) sınandı; gerçek tarayıcıda Order açıkken Return kapalı → açık → kapalı → yeniden açık ve Return önce → Order sonra geçişleri manuel yenilemesiz doğrulandı. Başlatma sırası önemli değildir: Order açıkken Return sonradan erişilebilir olursa sonraki yenilemede keşfedilir.
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
using SagaFlow.Storage.InMemory.DependencyInjection;

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
builder.Services.AddSagaFlowInMemoryStorage();

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

## S2-6A: BigPicture relationship backend

Backend read model `Implemented`; S2 remains `Partial`. Definition relationships and runtime invocation relationships are distinct layers. Existing `GET /sagaflow/api/definition-graph?domain=...&process=...&version=...` is unchanged: `edges[].source` is the declaring process identity (`domain/name/version`), target fields retain the requested identity/version, `relationship` preserves `related`, `invokes` or legacy omission. A non-null `target` identifies a resolved definition node; null means unresolved, with the existing `failures` explanation. No remote node/version is invented. Discovery endpoint addresses remain excluded from Dashboard DTOs. A static `invokes` edge does not start a Saga or manufacture a runtime relationship. Real HTTP/gRPC coverage: [`DefinitionDiscoveryIntegrationTests`](../tests/SagaFlow.Hosting.Tests/DefinitionDiscoveryIntegrationTests.cs); existing YAML/codec/version navigation contracts remain intact.

Runtime reads reuse `SagaStepState.Invocation/InvocationProgress/Reconciliation` and `SagaInstance.IncomingInvocation`; no registry, relationship store, coordinator or notification service is added. [`InstanceRelationshipView`](../src/SagaFlow.Hosting/Dashboard/InstanceRelationshipView.cs) is an allowlisted presentation model, not persisted state. The instance-scoped route avoids any global `ListAsync` or discovery call:

```text
GET {dashboardPath}/api/instances/{instanceId}/relationships
    ?domain=order&process=order-process&version=1.0
    &perspective=Source&offset=0&limit=50
```

`instanceId` is required and always **local**. Optional domain/process/version filters apply exactly to that local owner, not to the remote end of the relationship. Mismatching identity or missing instance returns 404. `perspective` is optional and case-sensitive (`Source` or `Target`); invalid values, negative/noninteger offset, or limit outside 1..100 return 400. Defaults: offset 0, limit 50. Response: `{ localInstanceId, items, nextOffset }`, where nextOffset is null on the last page. Ordering is incoming Target first, then source steps in definition order. A plain local Saga returns an empty items array; pagination does not turn a static definition reference into a runtime edge. Only GET is supported (405 otherwise).

Both the automatic embedded middleware (including a configured Dashboard path) and explicit `/sagaflow` endpoint mapping share this handler. Existing instance list/detail DTOs are unchanged. This is a bounded **single-instance read**, not a process-wide or global relationship index. It snapshots at most one local Saga via `FindAsync`; existing store snapshot cost depends on that instance's size, and offset pagination is not a cross-request transactional snapshot. The existing unpaged instance list API is not redesigned here.

Each item contains `invocationId`, `perspective`, source/target `{domain,process,version}`, `sourceSagaInstanceId`, `sourceStep`, `sourceCorrelationId`, nullable `targetSagaInstanceId`, `targetCorrelationId`, `lastKnownStatus`, `targetLocalStatus`, `createdAt`, `updatedAt`, `lastObservedAt`, and nullable `reconciliation {reason,requiredAt}`. Invocation ID is the logical relationship identity; preserve perspective/local owner when displaying observations from different hosts. Different invocations between identical definitions are not collapsed. Existing engine dedup/claim semantics remain responsible for one invocation per executable step and one incoming target claim. No cross-host merge service exists.

Source perspective exposes only its own recorded progress: target ID stays null until known, target correlation/local status are null, `lastKnownStatus` is a local observation of remote activity, **not authoritative live remote state**. Target perspective exposes its local Saga ID/correlation/status; lastKnownStatus/lastObservedAt/reconciliation are null rather than synthesizing source observations. Target createdAt is the incoming source-request creation time; updatedAt is the target Saga local update time. Source timestamps retain S2-5 progress semantics and may be null for legacy metadata. Local process/version, source Saga/step/correlation linkage are checked; inconsistent legacy linkage is omitted without inventing an identity. Remote process identities use the existing validated invocation contracts.

Only observed identity/status/timestamp/reconciliation fields are serialized: no request/context/step payload, publication errors, routing endpoints, attempt IDs, dedup ledger or connection configuration. Reconciliation reason remains the existing operator-facing text; this does not introduce arbitrary application-text redaction or production authentication. Existing host/network access responsibility remains. Each domain owns its state; reading Source does not query Target. BigPicture provides traceability, not execution or recovery controls. The existing `SagaInstanceChanged(localInstanceId)` SignalR lifecycle triggers rereading this same route; no global broker subscription or new event type is added.

InMemory state/claims/observations vanish on host restart; this read model cannot recover them. S2-6B graph/Drawer client integration and S2-6C sample/end-to-end acceptance remain `Planned`; no React/TypeScript or Procurement sample was added in S2-6A. Test evidence and actual run results are recorded in [project status](01-PROJECT_STATUS.md). Ownership/lifecycle context remains in [local coordination](04-local-saga-coordination.md).

### S2-6B: Definition View and Instance Context UX

BigPicture graph/runtime UX `Implemented`; browser/RabbitMQ acceptance of the new BigPicture flow remains S2-6C `Planned`. Existing React Flow, process cards, workflow diagram and overlay Drawer are reused. Definition View includes both `related` and `invokes`: related is neutral/dashed, invokes violet/directed, unresolved remains dashed, and matching loaded runtime invokes edges are highlighted green. A definition edge alone is never execution evidence. Existing zoom/pan/fit-view and resolved-node workflow navigation in Definition View remain; in Instance Context a node opens details, with an explicit **Open workflow definition** action. Edge and loaded relationship selection open the same dismissible Drawer, not a permanent panel.

Local Instance Detail offers **View instance in Process Explorer** using only its recorded local ID/domain/process/version. Context URL: `{dashboardPath}/bigpicture?instanceId=...&domain=...&process=...&version=...`. Workflow subroutes retain that query; browser back/forward restores it. **Open local instance** navigates to `{dashboardPath}/instances?instanceId=...`, which supports direct detail loading. Unknown remote instances never receive a local route or a fabricated Dashboard URL. Clearing context returns to Definition View; missing/incomplete context does not issue a relationship query. A local context without static references still shows its available definition and preserves unmatched runtime records.

[`getInstanceRelationships`](../src/SagaFlow.Dashboard.Client/src/api/sagaflowApi.ts) uses the S2-6A instance-scoped route with exact owner filters and `limit=100`. [`useInstanceRelationships`](../src/SagaFlow.Dashboard.Client/src/live/useInstanceRelationships.ts) loads one page at a time, uses nextOffset only on **Load more relationships**, marks incomplete results as partial/loaded rather than an exact total, and deduplicates only by perspective + invocation ID. Two invocations to the same target remain separate. Context changes abort/ignore stale requests; requests are serialized and refresh notifications coalesced. Missing/error/empty/loading states are explicit; failed refreshes clear obsolete runtime highlights. Invalid response ownership or non-progressing pagination produces an error.

Runtime matching requires the exact source/target process/version and an `invokes` declaration. Matching records highlight existing edges/nodes without resolving a missing definition. Unmatched records remain selectable below the canvas with **No matching declared invokes edge**; no process node or definition metadata is invented. Counts refer only to loaded observations for the selected local instance. All invocation details stay in the Drawer rather than filling the graph. Drawer selection survives discovery resolution and runtime status refresh, without resetting keyboard focus on each refresh. If refreshing/resetting pagination removes the selected record from the loaded page, the Drawer retains its ID and shows an explicit unloaded explanation without stale status. Changing the instance context resets the selection.

[`RuntimeRelationshipDetails`](../src/SagaFlow.Dashboard.Client/src/components/BigPicture/RuntimeRelationshipDetails.tsx) presents invocation ID, source/target identities, Saga IDs, source step, independent correlations and timestamps with copy controls for known IDs. Source uses **Last Known Remote Status** and explicitly states that Accepted is not Completed or authoritative live remote state; Target uses **Target Local Status**. Null target ID/correlation/time remains unknown/not recorded with no fake copy/link. Source reconciliation is a distinct warning and remains visible after a remote Completed observation. BPI-1D-02 (`Implemented`, finding `CLOSED`): optional nullable API `rejectionReason` appears as a separate technical **Rejection reason** only when present on a Source relationship. Null/missing reasons add no empty row; business Result is never consulted. Source Instance Traceability follows the same rule. [`rejectionReason.test.tsx`](../src/SagaFlow.Dashboard.Client/tests/rejectionReason.test.tsx) covers both panels with reason/null and decoy business results; real Procurement HTTP assertions verify the camelCase field separately from Result=null. Long reasons wrap within the existing layout. Current tests/build and browser regression scope are recorded in [project status](01-PROJECT_STATUS.md). Only the local perspective's Saga gets local navigation. Existing dark/light classes, viewport-bounded scrolling Drawer, close button and Escape behavior are retained.

The existing host-local `SagaInstanceChanged`/reconnect subscription is reused only with context. Matching events reread local observations; unrelated instance events do not fetch runtime pages. Refresh resets to page one, so results are marked partial again when appropriate. No new event, remote hub, global runtime index or execution/messaging change exists. InMemory/restart and access/auth limits from S2-6A remain. Source status advances only when its own local observation changes.

Evidence: [`bigPictureRuntime.test.tsx`](../src/SagaFlow.Dashboard.Client/tests/bigPictureRuntime.test.tsx), [`bigPictureGraph.test.tsx`](../src/SagaFlow.Dashboard.Client/tests/bigPictureGraph.test.tsx), [`workflowNavigation.test.tsx`](../src/SagaFlow.Dashboard.Client/tests/workflowNavigation.test.tsx), [`instanceLiveUpdates.test.tsx`](../src/SagaFlow.Dashboard.Client/tests/instanceLiveUpdates.test.tsx). These cover the API client contract, stale/context isolation, paging, notification coalescing, errors/empty states, duplicate identity, graph matching, node/edge Drawer, clipboard/local navigation, responsive/theme DOM and history regressions. Actual run counts/build logs are in [project status](01-PROJECT_STATUS.md); mocked React Flow/API DOM checks do not prove real RabbitMQ browser acceptance.

S2-6C must verify the new BigPicture flow with real two-host broker data: source and target deep links; two invocations to the same process and another target; unknown target ID; unmatched/unresolved definitions; reconciliation after remote completion; real SignalR update while Drawer remains open; actual clipboard; back/forward/version navigation; light/dark and narrow-screen Drawer/zoom/pan. Existing S2-5 browser regression is separate evidence and does not close these S2-6C scenarios. Procurement/sample work, federation and durable recovery were not added. S2 remains `Partial`.

### S2-6C: real RabbitMQ / HTTP / Chrome acceptance

2026-11-05: S2-6 local relationship visibility `Implemented`; the earlier S2-6B entry's pending browser acceptance is superseded by this run. S2 as a whole stays `Partial`. [`BigPictureAcceptanceTests.BigPictureActualBrokerHttpAndOptionalChromeAcceptance`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/BigPictureAcceptanceTests.cs) reuses the existing isolated RabbitMQ fixture and starts two main, independent WebApplication/IHost/DI containers/state stores/managed broker connections with separate loopback Dashboard URLs. **Both hosts run inside the same test OS process**; separate OS-process/crash isolation is not claimed. Each host has one local runtime process, preserving the existing transport constraint. Additional definition-only publisher, reconciliation, plain-local and high-volume fixture hosts serve specific acceptance cases.

The normal path uses real InvokeAsync → RabbitMQ request/acceptance → target participant command → real outcome publication → target terminal notification → source external-step continuation. A two-step source produces two distinct invocations to the same target process: one Completed while the second is Accepted/target-local Running, then source reaches Completed after the second target outcome. A separate source instance and its target remain unchanged: the complete source/target relationship HTTP JSON is compared before/after. Source/target Saga IDs, process/version, source step, invocation IDs and independent correlations are read from actual HTTP APIs. Target incoming metadata remains target-owned; source targetCorrelationId stays null, and Source last-known is never labelled authoritative live remote status.

Definition acceptance uses actual HTTP/2 gRPC publication/discovery: one resolved invokes target, a separate resolved related catalog publisher, and an intentionally unreachable invokes definition. Browser checks direction, marker, neutral/dashed related, violet invokes, green loaded-runtime edge, Local/Remote, and unresolved status without a fabricated resolved node. Definition View makes no runtime relationship request. Target incoming records without a matching static edge remain selectable without adding an invented source definition. A third real source invocation is paused by a test-only local handler after prepare and before publication, proving null target ID/lastObservedAt and no fake target copy/link. A real plain local Saga proves empty relationships.

Reconciliation is produced by closing only the legacy source participant publisher channel while its invocation channel remains operational. Real remote execution subsequently completes; source HTTP status remains Failed, the local observation becomes Completed, and the warning remains visible in the same Drawer. No normal-flow Saga status, invocation progress or reconciliation record is manually manufactured.

The **101-record pagination case is a separate seeded InMemory fixture**, not cross-domain execution evidence. It uses the actual HTTP API and Chrome UI to verify 100 + 1 pages, nextOffset, explicit partial labeling, distinct invocation IDs, and limit=101 rejection. No request is made to a global runtime index. Normal E2E is not expanded to 101 business invocations.

[`verify-bigpicture-ux.mjs`](../scripts/verify-bigpicture-ux.mjs) reuses Playwright 1.64.1 from ignored `.artifacts/s25-browser` and installed Chrome 156.0.8078.80, headless. It exercises actual Instance Detail → BigPicture, node/edge/relationship Drawer, direct context URL, workflow diagram, browser back/forward, real clipboard, light/dark, 390px, zoom/pan/fit, error/loading/empty states and long ID wrapping/scrolling. Real engine notifications are observed in forwarded SignalR frames and cause real relationship API rereads while the Drawer remains open, with zero main-frame navigation. Duplicate notifications do not create rows. A transport proxy forwards backend messages unchanged, then closes one real connection to verify automatic reconnect/requery; no SignalR messages are fabricated. A latency test delays an **actual fetched HTTP response**, changes SPA context, and releases it to verify stale response isolation. Clearing context closes the subscription and a subsequent real host notification causes no runtime query.

Visual inspection found an actual defect: sibling SVG paths could overpaint another edge's label and weaken the runtime highlight. The geometry-based browser regression first failed in `.artifacts/s26c-label-regression-confirmed.log`. The minimum UI correction uses [`ProcessRelationshipEdge`](../src/SagaFlow.Dashboard.Client/src/components/BigPicture/ProcessRelationshipEdge.tsx): existing smooth-step geometry/BaseEdge with opaque labels in React Flow's EdgeLabelRenderer layer, accessible button selection, and runtime path stacking. No API, state, execution or messaging contract changed. The corrected browser assertion checks that the label is unobscured and keyboard-selectable; screenshots were inspected separately for text/line overlap and contrast.

Reproduce after building the Dashboard assets and solution (existing Chrome/Docker required):

If the ignored S2-5 Playwright directory is absent on a fresh checkout, first run `npm install --prefix .artifacts/s25-browser playwright@1.64.1 --no-audit --no-fund`. No production dependency or separate browser framework is installed.

```powershell
$env:SAGAFLOW_RABBITMQ_USERNAME='sagaflow'
$env:SAGAFLOW_RABBITMQ_PASSWORD='sagaflow-dev'
$env:SAGAFLOW_BIGPICTURE_BROWSER='true'
./scripts/run-rabbitmq-integration.ps1 -Filter 'FullyQualifiedName~BigPictureActualBrokerHttpAndOptionalChromeAcceptance'
```

Without the browser flag, the test runs broker/HTTP assertions but does not claim browser execution. Runtime evidence, 19 screenshots and process log are ignored under `.artifacts/s26-acceptance/`: `browser-result.json`, `http-result.json`, `visual-review.json`; primary screenshots include `01-definition-light.png`, `02-source-edge-two-invocations.png`, `04-source-drawer-dark.png`, `05-source-drawer-narrow.png`, `06-live-completed-open-drawer.png`, `07-target-incoming-unmatched.png`, `09-reconciliation-remote-completed.png`, `11-pagination-partial-fixture.png`, and `15b-definition-narrow-zoom-pan.png`. Screenshots alone and seeded pagination do not prove the broker chain; the HTTP/broker test and actual browser observations provide separate evidence. Test host URLs are ephemeral, not application endpoints to retain.

Actual final counts, interim failures and log paths are in [project status](01-PROJECT_STATUS.md). Scope remains generic local traceability: InMemory state/claims/observations disappear on host restart; local events are best effort; no global state federation, durable delivery/recovery, new auth or operator execution controls were added. Existing host/network access responsibilities and operator-facing reconciliation text remain. S2-7 Procurement → Order experimental PoC/business acceptance is `Planned`; no Procurement sample is part of S2-6C.

#### S26-R01 final review resolution

2026-11-05: `CLOSED`, presentation fix `Implemented`. [`RuntimeRelationshipDetails`](../src/SagaFlow.Dashboard.Client/src/components/BigPicture/RuntimeRelationshipDetails.tsx) now reuses the existing OriginBadge to show **Origin: External** only for Target-perspective incoming invocation details. Target rows already originate exclusively from the local IncomingInvocation projection; no API origin field was added. Source observations and ordinary local Saga definition Drawers receive no external origin badge. Identity, correlation, timestamp and status fields are unchanged. The failing Target-origin regression first produced 21 passed/1 failed; after the fix, focused Drawer/graph tests passed 22/22 and the Dashboard suite 93/93, including negative Source/local assertions, preserved Target Local Status/correlations and light/dark badge classes. TypeScript and production build passed; runtime/browser tests were not rerun for this presentation-only fix. Earlier S2-6C broker/browser evidence remains separate historical evidence. [Current verification record](01-PROJECT_STATUS.md).

## Historical scope and thesis alignment (definition/discovery foundation)

This implements the structural Declarative Process Definition foundation and a static Big-Picture discovery capability. Domain ownership and in-process placement follow the Domain Starter component responsibilities. gRPC discovery is an optional embedded capability, not a new architectural container. Extending references with an optional version pin is an explicit deterministic lookup policy, not an execution dependency.

This document describes the definition/discovery feature, which never executes a process. A subsequent [local coordination PoC](04-local-saga-coordination.md) adds explicit `StartAsync`, forward command/outcome handling, Boolean condition evaluation, volatile instance state, and compensation planning through separate runtime classes. Broker delivery, durable state, compensation execution, restart recovery, durable idempotency, runtime traces, a dashboard graph, authentication provisioning, watchers/hot reload, and the five-service representative prototype remain outside the implementation. Static discovery remains distinct from runtime execution observability.

## S2-1 invocation contracts and review resolution

Contract/context foundation: `Implemented`; cross-domain runtime: `Planned`; S2 remains `Partial`.
[`ProcessDefinitionValidator`](../src/SagaFlow.Engine/ProcessDefinition/ProcessDefinitionValidator.cs) now accepts `related` and `invokes`; legacy omission is preserved. Only explicit `invokes` with an exact version authorizes target resolution. Discovery endpoint is never a runtime routing address. Canonical YAML/snapshot and real gRPC preservation are covered by [`CrossProcessDefinitionDiscoveryTests`](../tests/SagaFlow.Hosting.Tests/CrossProcessDefinitionDiscoveryTests.cs).

[`SagaStepExecutor`](../src/SagaFlow.Engine/Coordination/SagaStepExecutor.cs) runs optional [`ILocalSagaStepHandler`](../src/SagaFlow.Engine/Coordination/LocalSagaExecution.cs) handlers before forward command publication. Register a scoped handler in the Domain Starter, inject ISagaFlow, then call InvokeAsync(target, payload) from ExecuteAsync. Participants need no source execution context. [`LocalSagaExecutionBoundary`](../src/SagaFlow.Hosting/DependencyInjection/LocalSagaExecutionBoundary.cs) creates a fresh async DI scope per command, binds the engine instance/step, and clears it in finally on return, exception or cancellation. No AsyncLocal/static context exists. Calls outside this boundary fail. Escaped ISagaFlow references become invalid when the handler returns; fire-and-forget handler work is unsupported. No registered handlers preserves existing StartAsync dispatch behavior; handler exceptions follow dispatch failure policy.

[`SagaCoordinator`](../src/SagaFlow.Engine/Coordination/SagaCoordinator.cs) validates current step and local ownership in an atomic store update. Saga must be Running and the same step Dispatched. [`SagaStepState.Invocation`](../src/SagaFlow.Core/State/SagaStepState.cs) stores one immutable prepared request per executable step. Repeating identical target/payload retains Invocation ID; changed target/payload fails. Different steps/instances obtain independent records. The in-memory snapshot preserves these records; no durable adapter/recovery was added. Receipt Prepared means local registration only, with no remote acceptance or message publication.

[`ProcessInvocation`](../src/SagaFlow.Core/Messages/ProcessInvocation.cs) validates UUID invocation/message IDs, process identifiers/version, required correlation and Saga IDs. Accepted requires target Saga ID; terminal outcomes allow only Completed/Failed/Compensated. OccurredAt is producer UTC event time; relationship UpdatedAt is local observation time. Target Saga ID may be absent before acceptance. Message ID identifies one logical message and must survive redelivery; Invocation ID identifies the entire interaction. The full source/target/correlation identity allows ValidateAgainst to reject mismatches. S2-2 must implement receiver deduplication and atomically reject conflicting acceptance/terminal outcomes. Request generation is local preparation, not a send.

Evidence: [`InvocationExecutionTests`](../tests/SagaFlow.Hosting.Tests/InvocationExecutionTests.cs), [`InvocationValidationTests`](../tests/SagaFlow.Core.Tests/InvocationValidationTests.cs), [`ProcessInvocationContractTests`](../tests/SagaFlow.Engine.Tests/ProcessInvocationContractTests.cs). Cross-domain publishers/consumers, remote StartAsync, terminal propagation, persistence and Dashboard runtime relationships remain S2-2 or later.
