# Local Saga Coordination ve InMemory instance yönetimi

> Tarihsel ilk PoC kapsamı: transport, compensation yürütümü ve dashboard hakkındaki aşağıdaki yokluk ifadeleri bu geliştirme aşamasını anlatır. Güncel uygulama ve test durumu için [01-PROJECT_STATUS.md](01-PROJECT_STATUS.md) esas alınır.

Bu geliştirme, Domain Starter içine gömülü ilk çalışan ileri Saga yürütümünü sağlar. YAML yükleme/validation, Registry ve gRPC tanım keşfi korunur. Mesaj göndermek için yalnızca `ISagaMessageBus` portu kullanılır; kütüphanede varsayılan transport veya InMemory Message Bus yoktur.

## Public API ve kullanım

```csharp
using SagaFlow.Core.Abstractions;

public sealed class OrderService(ISagaFlow sagaFlow)
{
    public async Task<Guid> CreateAsync(OrderRequest request)
    {
        var order = await SaveOrderAsync(request);
        return await sagaFlow.StartAsync(order);
    }
}
```

Buradaki domain tipleri uygulamaya aittir. Domain Service katılımcıları command adlarını, step veya compensation kararlarını vermez. Core sözleşmesi:

```csharp
public interface ISagaFlow
{
    Task<Guid> StartAsync<TPayload>(TPayload payload,
        CancellationToken cancellationToken = default);
}
```

`AddSagaFlow` ile yapılandırılmış host, başlangıçta mevcut loader/registry mekanizmasını kullanır. `StartAsync` tam olarak bir `Local` tanım ister; sıfır yerel tanım veya birden fazla yerel süreç/sürüm açık hata üretir. Remote kayıtlar seçim dışında kalır. Latest-version seçimi veya trigger subscription yoktur.

Uygulama gerçek bir `ISagaMessageBus` adapter'ını DI'a kaydetmelidir. Bu geliştirmede böyle bir adapter sunulmaz. Adapter yokken host YAML yükleyebilir ve tanım keşfi/dashboard hosting çalışabilir; `StartAsync` ise instance yaratmadan açık eksik-transport hatası verir. Testlerdeki kayıt yapan fake'ler yalnızca test projelerindedir.

## Instance ve ileri step modeli

Mevcut `SagaInstance` genişletilmiştir:

- `Id`: mevcut string API'si korunarak Guid'nin `N` biçiminde saklanır; `StartAsync` Guid döndürür. `ISagaStateStore.FindAsync(Guid)` kolaylığı da vardır.
- `DefinitionName`, `DefinitionDomain`, `DefinitionVersion`: tanımın tam kimliği; süreç ilerlerken yalnızca bu yerel kayıt kullanılır.
- `CorrelationId`, `Status`, `CurrentStep`, `CreatedAt`, `UpdatedAt`, `FailureReason`.
- `Context`: başlangıç payload'ının bağımsız `JsonElement` karşılığı; System.Text.Json web/camelCase ayarları kullanılır. Null, primitive, array ve object JSON desteklenir; döngülü veya desteklenmeyen payload reddedilir.
- `Steps`, `Compensations`, `Revision`: dinamik state listeleri ve store concurrency token'ı.

Her YAML executable step'i için tek `SagaStepState` oluşturulur. `StepName` korunmuştur; `StepId` aynı YAML adını döndürür. State alanları `Command`, `ExecutionOrder`, `Input`, `Result`, `StartedAt`, `CompletedAt`, `TimeoutLimit`, `TimedOutAt` içerir. Başlangıçta hepsi `Pending` ve sırasızdır. Ayrı `SagaStepStatus` enum'u `Pending`, `Dispatched`, `Completed`, `Failed`, `TimedOut` değerlerini içerir; eski scaffold'ın genel `SagaStatus` alanı yerine ileri yürütüme özgü durumlar kullanılır.

Tek bir hosted timeout tarayıcısı, `TimeProvider` zamanına göre aktif `Dispatched` step'leri kontrol eder; instance başına timer oluşturmaz. Limit dolduğunda atomik store güncellemesi step ve Saga durumunu `TimedOut` yapar, `TimedOutAt` değerini kaydeder ve `CurrentStep` değerini korur. Yeni command, retry, compensation veya YAML `timeout` transition'ı yürütülmez. Timeout'tan sonra gelen outcome duplicate sayılmaz ve operatör çözümü için transport tarafından requeue edilir.

Engine, aynı doğrulanmış limiti broker-independent `SagaCommand.TimeToLive` metadata'sına taşır. RabbitMQ adapter bu değeri per-message `BasicProperties.Expiration` milisaniye alanına dönüştürür; timeout tanımlanmayan command'larda expiration boş kalır. Broker TTL yalnızca Ready mesajın kuyrukta tüketilebilirliğini sınırlar ve Engine'in bağımsız timeout kararının yerine geçmez.

İlk komut `steps` listesinin ilk elemanından seçilir. Sonraki adımlar `transitions.nextStep` ile belirlenir ve gerçek dispatch sırası 1'den başlayarak atanır. Her command aynı başlangıç JSON context'ini taşır; sonuçlar step'in `Result` alanında saklanır, context'e otomatik merge veya veri eşleme yapılmaz. Çalışmayan dalların state'i `Pending` kalır. Aynı step bir instance içinde ikinci kez dispatch edilemez.

## StartAsync ve dispatch sınırı

1. Tek yerel tanımı seç ve transport kaydını doğrula.
2. Payload'ı serialize et, instance/correlation kimliklerini oluştur.
3. Bütün step ve isteğe bağlı compensation state kayıtlarını oluştur; Saga'yı `Running`, aktif adımı listedeki ilk step yap.
4. Instance'ı store'a kaydet; başlangıç kaydında bütün adımlar `Pending`'dir.
5. Store'un atomik callback'i içinde ilk step'i claim et: `Dispatched`, gerçek sıra, input ve başlangıç zamanı atanır; `SagaCommand` hazırlanır.
6. Store kilidi bırakıldıktan sonra `ISagaMessageBus.PublishAsync` çağrılır. Başarılı çağrının ardından Guid döndürülür.

`Dispatched`, bu PoC'de bir dispatch denemesinin claim edildiğini gösterir; broker tarafından teslim garantisi değildir. Adım publish çağrısından **önce** `Dispatched` olarak işaretlenir ve publish await edilirken bu durumda görülebilir. Publish başarısız olur veya iptal edilirse, hâlâ `Dispatched` durumundaki step `Failed`, Saga da `Failed` yapılır; `FailureReason` yayın hatasıyla kaydedilir ve `SagaDispatchException` instance ve step kimliğiyle dışarı iletilir. Transport senkron olarak outcome teslim etmişse önceden kabul edilmiş tamamlanma geçmişi silinmez. Başlangıç kaydı ile ilk claim arasındaki iptal de Running orphan bırakmaz. Claim öncesi iptal edilen adımlar Pending kalabilir, Saga Failed olur.

State ve transport arasında dağıtık/kalıcı transaction yoktur. Adapter mesajı kabul edip ardından hata verirse teslimatın kesin durumu bilinemez. Otomatik retry veya replay yoktur; bu mekanizma uçtan uca exactly-once garantisi değildir. Publish ve state-store hataları gizlenmez.

## Outcome ve koşullar

Gerçek bir transport adapter'ı scope içinden `OutcomeEventReceiver` alıp `ReceiveAsync` çağırabilir. Mesaj sözleşmesi:

```csharp
await receiver.ReceiveAsync(new SagaOutcomeEvent
{
    Headers = new SagaMessageHeaders
    {
        SagaInstanceId = instance.Id,
        CorrelationId = instance.CorrelationId,
        StepId = instance.CurrentStep!
    },
    IsSuccess = true,
    Result = JsonSerializer.SerializeToElement(new { available = true })
});
```

Instance/correlation/step eşleşmesi, aktif Saga ve step'in `Dispatched` olması atomik güncelleme içinde doğrulanır. Tamamlanmış/başarısız step outcome'u tekrar kabul edilmez. Aynı outcome eşzamanlı gelse de yalnızca biri yeni transition ve command oluşturur. Bu, instance bellekte kaldığı sürece yerel duplicate korumasıdır; katılımcı business side effect idempotency'si veya durable inbox değildir.

`IsSuccess`, YAML'daki `success`/`failure` kategorisine eşlenir; mevcut `Name` alanı routing/subscription mekanizması değildir. `TransitionResolver`, mevcut `ConditionParser` AST'sini kullanır. Parser yalnızca sözdizimini ayrıştırır; `ProcessDefinitionValidator` YAML yüklenirken yalnızca `result.*` köklü, case-sensitive Boolean property yollarını kabul eder. `data.*`, `context.*` ve köksüz koşullar process, step ve condition bilgisiyle reddedilir. Boolean `==`/`!=` ve iç içe property yolları desteklenir. Çağrı/reflection/scripting yoktur. Runtime'da eksik property, yanlış JSON tipi veya savunma amaçlı kök kontrolü açık hata üretir; sessizce false veya else kabul edilmez.

Eşleşen kategorinin koşulları önce değerlendirilir; eşleşme yoksa aynı kategorideki `else: true` veya unconditional default kullanılır. YAML liste sırası kararı değiştirmez. Yanlış metadata veya değerlendirilemeyen sonuç state'i değiştirmeden reddedilir; doğru sonuç daha sonra işlenebilir. Kategoride hiçbir kural yoksa diğer kategorinin fallback'i kullanılmaz.

Kabul edilen outcome'un forward state'i ve bir sonraki command claim'i tek atomik güncellemede saklanır. `completed` Saga'yı `Completed`, `compensation` ise `CompensationRequested` yapar; terminal durumlarda CurrentStep temizlenir. Validator, listedeki ilk step üzerinden erişilebilen executable graph üzerinde DFS aktif yol kontrolüyle self-loop ve çok adımlı döngüleri, terminal çıkışları olsa bile reddeder; hata döngünün step adlarını içerir. Tamamlanmış DFS düğümlerine birleşen dallar geçerli DAG yapılarıdır. Önceki erişilebilirlik/terminal yol kontrolleri ve exhaustive koşullardan sonraki erişilemez fallback'in dışlanması korunur. Runtime'ın tek-seferlik dispatch savunması da korunur: daha önce çalıştırılmış bir step'e dönüş Saga'yı `Failed` yapar ve açık cycle/revisit hatası üretir.

## Compensation planı

YAML `SagaCompensationDefinition` tanımsal modeldir. Ayrı `SagaCompensationState`, sadece compensation tanımlı step'ler için oluşturulur. Alanları `StepId`, `Command`, `Status`, `ExecutionOrder`, `StartedAt`, `CompletedAt`, `Result` şeklindedir. Durumlar bu sürümde `Pending` ve `Planned`'dır.

Compensation istendiğinde yalnızca `Completed` forward step'lerin compensation kayıtları planlanır. Sıra, forward `ExecutionOrder` değerinin tersidir; planın kendi sırası 1'den başlar. Başarısız veya hiç çalışmamış adımlar ve compensation tanımı olmayan adımlar plana alınmaz. `ProcessStateManager.GetCompensationPlan` sıralı bağımsız snapshot döndürür. Forward `Completed` bilgisi korunur. Compensation command'ı publish edilmez; StartedAt/CompletedAt/Result yürütüm yapılmadığı için boş kalır.

## InMemory Store ve concurrency

Güncel Return incelemesi (REV-02, 2026-11-03): outcome uygulandıktan sonra sıradaki command publish hatası [`SagaStepExecutor`](../src/SagaFlow.Engine/Coordination/SagaStepExecutor.cs) tarafından `Failed` durumuna yazılır. [`RabbitMqTransport`](../src/SagaFlow.Messaging.RabbitMQ/RabbitMqTransport.cs) bu `SagaDispatchException` sonrasında önceki outcome'u ACK eder. Duplicate teslim, uygulanmış outcome'u yeniden yürütmeye yetmez; ACK yerine basit NACK recovery değildir. Publish sonucunun belirsiz kaldığı pencere ve InMemory host restart kaybı için otomatik uzlaştırma yoktur. Bu güncel sınır, aşağıdaki tarihsel ilk PoC kayıtlarını değiştirmez; [Return inceleme ayrıntısı](07-return-workflow.md) ve [Engine publish hata testi](../tests/SagaFlow.Engine.Tests/LocalSagaCoordinationTests.cs) kapsamı gösterir.

PA-01 test izolasyonu: [`scripts/run-rabbitmq-integration.ps1`](../scripts/run-rabbitmq-integration.ps1) mevcut `samples/docker-compose.yml` brokerının sağlıklı olmasını bekler, her koşu için benzersiz bir test vhost'u oluşturur, `sagaflow` test kullanıcısına bu vhost üzerinde izin verir ve iki entegrasyon bayrağıyla izole .NET build/test çalıştırır. `SAGAFLOW_RABBITMQ_USERNAME` ve `SAGAFLOW_RABBITMQ_PASSWORD` ortamda verilmelidir; script parola saklamaz. Windows PowerShell örneği: `$env:SAGAFLOW_RABBITMQ_USERNAME='sagaflow'; $env:SAGAFLOW_RABBITMQ_PASSWORD='<Compose parolası>'; ./scripts/run-rabbitmq-integration.ps1`. Script, örneklerin `/` vhost'unu değiştirmeden test process'lerine `SAGAFLOW_RABBITMQ_TEST_VHOST` ve `RabbitMq__VirtualHost` aktarır; sonunda yalnız oluşturduğu vhost'u siler. Transport fixture'ı ve brokerlı örnek testler bu iki ayar olmadan `/` üzerinde çalışmayı reddeder. Domain tabanlı `order.outcomes` ve ReplyTo adları değişmez; vhost sınırı kaynak çakışmasını önler. Test vhost'u silinemeyip uyarı verilirse adı logdan alınarak elle temizlenmelidir. [Transport fixture](../tests/SagaFlow.Messaging.RabbitMQ.Tests/RabbitMqBrokerFixture.cs) ve [örnek broker testleri](../tests/SagaFlow.Samples.Tests/SampleRabbitMqEndToEndTests.cs) yapılandırmayı doğrular.

`InMemorySagaStateStore`, `ISagaStateStore` portunu uygular. State process içindeki dictionary'de saklanır; her instance ayrı kilit kullanır. `SaveAsync` yeni kaydı revision 0 ile kabul eder; var olan kaydı yalnızca güncel revision eşleşiyorsa değiştirir. Stale read/modify/write sessiz veri kaybı yerine hata üretir.

Yeni `UpdateAsync<TResult>` senkron ve side-effect-free callback ile atomik değişiklik yapar. Callback bağımsız çalışma kopyasını alır; exception veya iptal varsa değişiklik commit edilmez. Callback içinde I/O, await veya nested state write yapılmamalıdır; aynı instance'a nested write açıkça reddedilir. Mesaj publish bu callback'in dışındadır, dolayısıyla transport senkron olarak outcome döndürse de store kilidinde deadlock oluşmaz.

Save, Find ve Update commit sınırlarında listeler, step/compensation nesneleri ve JSON değerleri deep-copy edilir. Dışarı dönen veya callback'ten tutulan referanslar kayıtlı state'i değiştiremez. `Revision` her başarılı yazmada artar. Coordinator yalnızca portu kullanır; gelecekte database adapter'ı aynı atomic-update/optimistic concurrency semantiğini sağlamalıdır. Şu anda database, migration, distributed lock veya restart recovery yoktur.

## DI kayıtları

| Servis | Lifetime |
|---|---|
| `ISagaStateStore` → `InMemorySagaStateStore` | Singleton; TryAdd ile değiştirilebilir |
| `ISagaClock` → `SystemSagaClock` | Singleton; değiştirilebilir |
| `ProcessDefinitionRegistry` | Singleton; önceki davranış |
| `TransitionResolver`, `ProcessStateManager` | Singleton; stateless |
| `SagaInstanceManager`, `SagaStepExecutor`, `CommandDispatcher` | Scoped |
| `SagaCoordinator`, aynı instance'a eşlenen `ISagaFlow` | Scoped |
| `OutcomeEventReceiver` | Scoped |
| `ISagaMessageBus` | Varsayılan kayıt yok; uygulamanın adapter'ı |

Scoped transport desteklenir; singleton coordinator içinde scoped transport tutulmaz. Store adapter'ı `AddSagaFlow` öncesinde veya sonrasında değiştirilebilir. Yeni paket veya proje eklenmemiştir.

## Testler ve sınırlar

```powershell
dotnet build .\SagaFlow.slnx --no-restore
dotnet test .\SagaFlow.slnx --no-build --no-restore
```

`LocalSagaCoordinationTests`, `RuntimeTransitionTests`, `InMemorySagaStateStoreTests` ve `LocalSagaCoordinationHostingTests` gerçek runtime sınıfları ve gerçek InMemory state store üzerinde çalışır; command publish yalnızca test fake'leri üzerinden doğrulanır. Hosting testleri gerçek YAML startup ve DI scope doğrulaması kullanır; gRPC/cross-process tanım keşfi testleri de korunmuştur.

İlk runtime geliştirmesinin referansı: solution build sıfır uyarı ve sıfır hatayla geçti. Toplam 157 test geçti (Core 1, Engine 121, Hosting 35); başarısız veya atlanan test yok. Önceki 98 test korundu; 59 yeni davranış vakası eklendi. Testler 2 ve 10 adımlı akışları, conditional/else dallarını, ters compensation planını, 500 eşzamanlı atomik state güncellemesini, 30 eşzamanlı duplicate outcome'u, scoped DI, transport hataları ve cancellation sınırlarını kapsar.

LC-01/LR-01/LC-02 review düzeltmelerinden sonra: odaklı Engine testleri 123/123 geçti. Yukarıdaki tam solution build komutu sıfır uyarı/hata ile, tam test komutu 178 başarılı testle geçti (Core 1, Engine 142, Hosting 35); başarısız/atlanan yok. Net 21 regresyon vakası eklendi: YAML condition kökleri, YAML'dan runtime Boolean değerlendirmesi, terminal çıkışlı döngülerin reddi ve DAG birleşmeleri. Eski parser örneği `result.*` ile hizalandı; terminal çıkışlı döngüyü kabul eden test ret beklentisine çevrildi. Runtime revisit testi artık geçerli bir DAG üzerinde tutarsız state geçmişi enjekte ederek mevcut dispatch savunmasını doğrular; hiçbir test kaldırılmadı veya devre dışı bırakılmadı. Publish failure davranışı mevcut ilk/sonraki command hata testleriyle doğrulanır; production runtime kodu değiştirilmedi.

## Dosya kapsamı

Core: yeni `ISagaFlow`, yeni `SagaCompensationState`; genişletilmiş `ISagaStateStore`, `SagaInstance`, `SagaStepState`, `SagaStatus`, `SagaCommand`, `SagaOutcomeEvent`, `SagaMessageHeaders`.

Engine: mevcut `SagaCoordinator`, `SagaInstanceManager`, `SagaStepExecutor`, `TransitionResolver`, `ProcessStateManager`, `CommandDispatcher`, `OutcomeEventReceiver` uygulamaları; yeni `InMemorySagaStateStore`, bağımsız snapshot yardımcısı, `SystemSagaClock` ve `SagaDispatchException`.

Hosting: mevcut `SagaFlowServiceCollectionExtensions` DI kayıtları. Testler: davranış suite'leri, ortak fixture ve discovery izolasyon fixture'ları. Dokümanlar: bu kılavuz ve definition-discovery kılavuzundaki güncel kapsam açıklaması. Production mesaj transport'u eklenmedi.

`await sagaFlow.StartAsync(payload)` kayıtlı test transport'u ile gerçekten instance oluşturur, bütün YAML adımlarını state'e aktarır, InMemory store'a kaydeder ve ilk command için portu çağırır. Gerçek broker yoktur; production host'ta adapter kaydı yoksa çağrı hata verir. InMemory verisi uygulama kapanınca kaybolur. Compensation execution/outcome/retry, durable inbox/outbox, participant mikroservisleri, restart recovery, çoklu process/sürüm seçimi ve runtime dashboard/observability kapsam dışıdır. Statik Big-Picture graph'ı runtime trace değildir ve tanım keşfi kodu değiştirilmemiştir.

## S2-2: invocation lifecycle ve instance ilişkileri

Yerel state/ilişki yönetimi `Implemented`; uzak mesajlaşma ve uzak sonucu bekleyen koordinasyon `Planned`. S2 `Partial` kalır. [`ProcessInvocationManager`](../src/SagaFlow.Engine/Coordination/ProcessInvocationManager.cs) mevcut [`ISagaStateStore.UpdateAsync`](../src/SagaFlow.Core/Abstractions/ISagaStateStore.cs) üzerinde kayıt, sorgu, gözlem ve hedef kaynak ilişkisi bağlama işlemleri sağlar. Public `InvokeAsync(target, payload)` yalnız S2-1 Domain Starter handler scope içinde çalışır; [`SagaCoordinator`](../src/SagaFlow.Engine/Coordination/SagaCoordinator.cs) kaynak instance/step'i context'ten alır ve manager'a verir. Kaynak parametreleri application API'sine eklenmedi.

Kaynak step'te `Invocation` immutable request (tam source/target/correlation kimliği, JSON payload ve ayrı Message ID) tek otoritedir; [`InvocationProgress`](../src/SagaFlow.Core/State/InvocationState.cs) yalnız status, nullable TargetSagaInstanceId, CreatedAt, UpdatedAt ve LastObservedAt tutar. Payload JSON olarak snapshot'tır; tekrar çağrı karşılaştırması Web JSON serializer çıktısının birebir eşitliğidir. Anlamsal olarak aynı fakat farklı sırada serialize edilen nesneler eşit sayılmayabilir. Aynı instance/step/hedef/payload için atomik tekrarlar mevcut kaydı döndürür; farklı hedef/payload reddedilir. Başka step veya instance bağımsız Invocation ID alır. Prepared receipt yalnız kaydın varlığını bildirir; daha sonraki gözlemler ilerlese bile tekrar InvokeAsync remote kabul iddiası üretmez.

`FindAsync` kaynak instance ve invocation ile ilişki snapshot'ını döndürür. `ObserveAsync` tam beklenen ProcessInvocationIdentity ile eşleşme ister, yerel process sahipliğini doğrular. İzinli geçişler: Prepared → Requested; Requested → Accepted veya Rejected; Accepted → Running veya Completed/Failed/Compensated; Running → Completed/Failed/Compensated. Running bildirimi gelmeden terminal bildirimi kabul edilebilir. Aynı status tekrarında UpdatedAt değişmez, LastObservedAt yenilenir. Diğer geriye geçişler ve çelişen terminal durumlar atomik hata üretir. Accepted/Running/terminal target Saga ID gerektirir; kabulde bağlanan kimlik sonradan değişemez. Prepared/Requested/Rejected üzerinde target ID bağlanamaz. Eksik gözlem yerel Saga başarısızlığı olarak yorumlanmaz.

Zamanlar engine ISagaClock üzerinden UTC gözlem/kayıt zamanıdır: CreatedAt ilk yerel kayıt, UpdatedAt son durum değişimi, LastObservedAt son açık observation çağrısıdır; Prepared oluşturulurken LastObservedAt null'dır. Kaynak gözlem saati geriye giderse update reddedilir. Uzak üretici saatine göre otomatik sıralama veya mesaj deduplication yapılmaz. Aynı invocation kimliği, gelecekte bir mesajın yalnız bir kez yayımlanacağını garanti etmez. Duplicate callback başarılı olduğunda store revision artabilir; mantıksal kayıt çoğalmaz.

Hedef domain `AttachIncomingAsync` ile **önceden oluşturulmuş kendi yerel instance'ına** [`IncomingInvocation`](../src/SagaFlow.Core/State/InvocationState.cs) ekler. Kaynak tam kimliği, kaynak request oluşturulma zamanı ve yerel kabul zamanı tutulur; target Saga ID sahibi `SagaInstance.Id` alanıdır. Target process/version sahipliği eşleşmelidir; review düzeltmesiyle yerel target correlation bağımsız kalır, kaynak correlation IncomingInvocation.Identity içinde korunur. Aynı kaynak/tarih tekrarı idempotent, farklı kaynağa yeniden bağlama hatadır. Kaynak instance store'una erişim veya yeni Saga başlatma yoktur. İki domainin kayıtları bağımsızdır; review düzeltmesiyle hedef store içindeki InvocationId claim, hedef instance ve incoming ilişki birlikte atomiktir. Domainler arası atomiklik yoktur.

Store snapshot'ları immutable request/progress/provenance kayıtlarını güvenle paylaşır; mutable instance/step alanları kopyalanır. Conflict, exception ve cancellation durumunda store commit etmez; başarılı receipt yalnız UpdateAsync döndükten sonra üretilir. Callback içinde I/O yoktur. Adapter aynı atomiklik ve optimistic revision sözleşmesini korumalıdır; durable adapter/restart recovery eklenmedi.

Kanıt: [`InvocationStateTests`](../tests/SagaFlow.Engine.Tests/InvocationStateTests.cs) gerçek InMemory store üzerinde concurrent hazırlama, conflict rollback, stale revision, lifecycle, target ilişki, snapshot ve cancellation sınar; [`InvocationExecutionTests`](../tests/SagaFlow.Hosting.Tests/InvocationExecutionTests.cs) gerçek Engine handler scope/public API ve concurrent tekrarları sınar. S2-3'e request publish/receiver dedup ve message-to-observation bağlama; S2-4'e uzak sonucu bekleme ve yerel transition entegrasyonu kalır. Dashboard state bildirimi, dağıtık atomiklik ve durable inbox/outbox güvencesi bu aşamada eklenmedi.

### S2-2 review: target claim ve publish eligibility

S2-2-01/02/03 düzeltmeleri: `Implemented`; S2 `Partial`. Normal `StartAsync → AttachIncomingAsync` target'ın ürettiği Saga ID ve yerel correlation'ı korur. Kaynak correlation yalnız incoming identity'dedir. Tam request alan overload, kaynak payload'ı da doğrular. S2-2 identity/timestamp overload uyumluluk için korunur: ilk bağlamada target business Context'ini payload kabul eder, tekrarında saklanan incoming payload kullanılır. Mesaj adaptörleri tam request overload'unu kullanmalıdır.

[`ISagaStateStore`](../src/SagaFlow.Core/Abstractions/ISagaStateStore.cs) `GetOrCreateIncomingAsync` ve `AttachIncomingAsync` atomik claim sözleşmeleri sunar. Desteklemeyen adapter varsayılan olarak NotSupportedException üretir; lookup-then-create fallback yapılmaz. [`InMemorySagaStateStore`](../src/SagaFlow.Engine/Persistence/InMemory/InMemorySagaStateStore.cs) store'a özel InvocationId → TargetSagaId indeksi ile claim işlemlerini seri yürütür. İlk request'in tam identity/payload'ı incoming kaydında tutulur. Aynı invocation tekrarında instance creation factory çağrılmaz; mevcut target snapshot döner. Aynı ID farklı source/target/correlation/payload ile gelirse conflict'tir. Başka var olan target'a aynı invocation bağlanamaz. Normal SaveAsync/UpdateAsync incoming provenance'ı değiştiremez veya silemez; indeks bu yollardan atlanamaz. Aynı içeriğin serialize/deserialize edilmiş kopyası normal revision kontrollü Save ile korunabilir.

[`ProcessInvocationManager.GetOrCreateTargetAsync`](../src/SagaFlow.Engine/Coordination/ProcessInvocationManager.cs) yalnız yerel target state oluşturur ve ilişkiyi bağlar; step dispatch etmez, yeni bir public StartAsync davranışı eklemez. SagaInstanceManager yeni local correlation ve bağımsız Saga ID üretir. Store factory'si senkron, side-effect-free olmalı; I/O veya store çağrısı yapmamalıdır. Factory, validation veya cancellation hatasında claim/instance/attachment commit edilmez. PostgreSQL gibi adapter'lar unique invocation constraint ile claim, instance ve ilişkiyi aynı transaction içinde uygulamalıdır. InMemory indeks process ömrüyle sınırlıdır; durable dedup, crash recovery veya exactly-once iddiası yoktur. Önceden iki kez çağrılmış normal StartAsync iki yerel Saga yaratabilir; ikinci attachment reddedilir. Gelecekteki receiver duplicate yaratmamak için atomik GetOrCreateTargetAsync sınırını kullanmalıdır.

`CanPublishAsync` yalnız yerel eligibility snapshot'ıdır: kaynak definition Local, identity/instance/step/correlation tutarlı, Saga Running, aynı current step Dispatched ve invocation progress Prepared olmalıdır. Failed/TimedOut/terminal veya artık aktif olmayan step yayınlanamaz. Prepared tek başına izin değildir. Requested/Accepted/terminal invocation yeni gönderime uygun sayılmaz; Requested retransmission/delivery belirsizliği politikası S2-3'te ayrıca tanımlanmalıdır. Bu sorgu rezervasyon değildir; kontrol ile I/O arasında yarış mümkündür. S2-3 publisher atomik reservation/recheck, cancellation/dispatch koordinasyonu ve teslimat dedup politikasını tanımlamalıdır. Bu aşamada yayıncı veya mesaj gönderimi eklenmedi.

Kanıt: [`IncomingClaimTests`](../tests/SagaFlow.Engine.Tests/IncomingClaimTests.cs) gerçek StartAsync/attach zinciri, local/source correlation ayrımı, concurrent duplicate claim, source/payload conflict, factory/validation/cancellation rollback ve stable target ID sınar. [`InvocationStateTests`](../tests/SagaFlow.Engine.Tests/InvocationStateTests.cs) eligibility durumlarını; [`InvocationExecutionTests`](../tests/SagaFlow.Hosting.Tests/InvocationExecutionTests.cs) gerçek başarısız handler sonrasında eligibility reddini doğrular. Güncel koşu sayıları [proje durumunda](01-PROJECT_STATUS.md).

## S2-3: cross-domain messaging ve outcome propagation

Durum: opt-in RabbitMQ invocation trafiği ve outcome altyapısı `Implemented`; S2 bütünü `Partial`. Önceki S2-1/S2-2 bölümlerindeki S2-3'e ertelenmiş messaging ifadeleri bu bölümle güncellenir; durable recovery ve S2-4 coordination hâlâ `Planned`.

### Yapılandırma ve ownership

`AddSagaFlow(...)` ardından `AddSagaFlowRabbitMq(options => { options.EnableProcessInvocations = true; ... })` veya host connection overload'u kullanılır. Varsayılan false mevcut participant trafiğini korur; kapalıyken InvokeAsync yalnız Prepared yerel kayıt oluşturur. Target domain scoped [`IProcessInvocationPolicy`](../src/SagaFlow.Engine/Messaging/ProcessInvocationReceiver.cs) kaydeder; `ValidateAsync(request, ct)` null döndürürse kabul, dolu gerekçe döndürürse red. Policy yoksa fail-closed rejection üretilir. Policy iş kurallarını domain içinde tutar; side-effect-free olmalıdır. Eşzamanlı duplicate teslimlerde validation birden fazla çalışabilir, ilk atomik kaydedilen admission kararı kazanır. Policy exception'ı rejection sayılmaz; transient işleme hatası olarak requeue edilir.

[`LocalSagaExecutionBoundary`](../src/SagaFlow.Hosting/DependencyInjection/LocalSagaExecutionBoundary.cs) bütün yerel handler'lar başarıyla tamamlandıktan sonra scoped publisher'ı çağırır; handler InvokeAsync sonrası exception/cancellation üretirse Prepared request gönderilmez. Application-facing InvokeAsync imzası ve Prepared receipt semantiği korunur. Target receiver uygulama iş mantığından ayrıdır ve domainin kendi registry'sindeki exact Local domain/process/version kimliğini doğrular. Atomik GetOrCreateTargetAsync yolunu kullanır; duplicate için StartAsync/AttachIncoming zinciri kurulmaz. Target kendi Saga ID/correlation değerini üretir, source correlation incoming identity'de kalır. Target forward step'leri Pending kalır; bu aşamada dispatch yapılmaz.

### Port, routing ve lifecycle

[`IProcessInvocationBus`](../src/SagaFlow.Core/Abstractions/IProcessInvocationBus.cs) mevcut request/accepted/rejected/terminal modellerini taşır; Engine RabbitMQ paketine bağımlı değildir. Opt-in adapter scoped [`ProcessInvocationPublisher`](../src/SagaFlow.Engine/Messaging/ProcessInvocationPublisher.cs) ve [`ProcessInvocationReceiver`](../src/SagaFlow.Engine/Messaging/ProcessInvocationReceiver.cs) servislerini Hosting scope'larına kaydeder. Alternatif transport aynı portu sağlayıp bu Engine servislerini scoped kaydedebilir.

[`RabbitMqInvocationTransport`](../src/SagaFlow.Messaging.RabbitMQ/RabbitMqInvocationTransport.cs) ayrı durable direct exchange `sagaflow.process-invocations` kullanır. Queue/routing key: `sagaflow.invocations.{SHA256(System.Text.Json ile ProcessIdentity UTF-8 serialization)}.requests` veya `.outcomes` (uppercase hex). Hash tam domain/process/version içerir; kimlik ayrıca body'de açık taşınır ve alıcıda doğrulanır. Her host tek Local tanım/sürüm için iki queue tüketir. Participant exchange/step queue'ları ve `{domain}.outcomes` yolu değiştirilmedi. gRPC endpoint hiçbir zaman mesaj adresine çevrilmez. Request ReplyTo, source identity'den türetilmiş outcome queue'dur; alıcı bunu doğrular, keyfi reply adresini takip etmez. Type, MessageId ve CorrelationId AMQP header'ları body metadata ile eşleşmelidir.

Host startup önce tanımları yükler, mevcut participant transport'u, sonra invocation transport'u açar. Invocation adapter ayrı publisher/consumer channel ve manual acknowledgement kullanır. Stop consumer'ları iptal eder, kendi channel'larını kapatır; bağlantı ownership mevcut provider'da kalır. Kuyruklar durable'dır; ilk kez hiç başlamamış target için routing mevcut olmayabilir. Mandatory publish ve publisher confirms açıktır; unroutable/confirmation hataları çağırana taşınır. Confirm yalnız broker publish sonucudur, domain kabulü veya Saga tamamlanması değildir.

### Publish reservation ve belirsizlik

Publisher source store UpdateAsync içinde gerçek local instance/step/correlation sahipliğini, Running Saga, current Dispatched step, Prepared progress ve boş ActiveAttemptId koşulunu kontrol eder. Tek atomik işlem Request durumunu Requested yapar ve attempt claim'i kaydeder. Bu commit publish'in yetkilendirme/linearization noktasıdır; broker I/O callback dışında yapılır. Eşzamanlı ikinci attempt reddedilir. Kaynak daha önce failed/timed-out/terminal olmuşsa claim alınamaz. Claim sonrasında source başarısız olursa zaten yetkilendirilmiş in-flight teslimi geri almak mümkün değildir; yeni attempt reddedilir. Bu sınır reservation yarış testinde açıkça sınanır.

Requested, attempt'in yetkilendirildiği anlamındadır; brokerın kesin kabulünü iddia etmez. Başarılı confirm sonrasında attempt temizlenir; erken gelmiş acceptance/terminal status geriye yazılmaz. Hata veya cancellation sonrası Requested korunur, LastError yazılır ve [`ProcessInvocationPublishException`](../src/SagaFlow.Engine/Messaging/ProcessInvocationPublisher.cs) SourceInstanceId/InvocationId ile çağırana döner. Yerel step bu transport belirsizliği nedeniyle otomatik Failed yapılmaz. Bookkeeping commit'i de başarısız olursa aynı exception türüyle belirsizlik bildirilir; aktif claim korunur ve kör retry engellenir. Bu durum manuel uzlaştırma gerektirir.

Retry yalnız explicit `PublishAsync(sourceId, invocationId, retry: true)` ile, kaynak hâlâ geçerliyse ve Requested/in-flight olmayan kayıtta mümkündür. Aynı request, InvocationId ve MessageId kullanılır. Accepted/Rejected/terminal kayıt yeniden request gönderemez. Otomatik retry, lease expiry, durable outbox veya dağıtık transaction yoktur. CanPublishAsync hâlâ snapshot sorgusudur; gerçek publisher atomik claim kullanır.

### Admission, response ve terminal sonuçlar

Store ilk admission kararını request identity/payload ve stable response MessageId ile saklar. Acceptance yayınında hata olsa bile target ve karar korunur; request redelivery aynı target ID/response metadata ile yeniden cevaplanır. Rejection target oluşturmadan saklanır; aynı invocation tekrarında domain kararı değişse bile red korunur. Farklı source/target/correlation/payload ile aynı invocation conflict'tir. Source/target store'ları bağımsızdır; yeni admission opsiyonlarını desteklemeyen adapter NotSupported üretir.

Source receiver kendi store'undaki tam beklenen invocation identity ile eşleşme ister ve status'u atomik ObserveAsync üzerinden uygular. `(InvocationId, MessageId)` için içerik fingerprint'i aynıysa duplicate idempotent, aynı MessageId farklı içerikle kullanılırsa conflict'tir. Accepted target ID gerektirir; rejection ile çelişen acceptance, farklı target ID ve çelişen terminal durumlar reddedilir. Terminal durumdan sonraki aynı hedefe ait geç acceptance state'i geriye götürmez. Yerel Saga status/current step ilerletilmez.

Terminal yayın altyapısı explicit `PublishTerminalAsync(targetId)` ile hazırdır: yalnız gerçek local Completed/Failed/Compensated target state ve incoming ilişkiyle çalışır; stable terminal MessageId state'te tutulur. Otomatik gerçek terminal event hook'u kurulmadı. Testler bu API'yi explicit terminal state güncellemesi sonrası çağırır; üretim akışında sahte terminal event üretilmez. Source Requested durumundayken terminal acceptance'tan önce gelirse Engine NotReady üretir; S2-3 review düzeltmesiyle RabbitMQ adapter sınırsız requeue yerine aşağıdaki durable parking/bounded reconciliation yolunu kullanır. Acceptance hiç gelmezse mesaj park halinde görünür kalır; otomatik recovery garantisi yoktur.

Malformed metadata, yanlış ownership, unknown invocation ve çelişen sonuçlar reject/no-requeue edilir (varsayılan dead-letter topolojisi eklenmedi). Transient işleme/yayın hataları kısa gecikmeyle nack/requeue edilir. InMemory state/claim/admission/message dedup process kapanınca kaybolur; durable queue tek başına restart recovery sağlamaz. Çoklu replica arasında paylaşılan durable store yoksa domain düzeyinde tek-instance dedup garantisi verilmez; stale source outcome'u veya restart sonrası yinelenen remote state için otomatik uzlaştırma yoktur.

Kanıt: [`ProcessMessagingTests`](../tests/SagaFlow.Engine.Tests/ProcessMessagingTests.cs), [`ProcessInvocationRoutingTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ProcessInvocationRoutingTests.cs), [`ProcessInvocationIntegrationTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ProcessInvocationIntegrationTests.cs). Entegrasyon testleri tek test process'i içinde ayrı IHost, DI container, state store ve managed RabbitMQ bağlantılarına sahip iki Domain Starter kullanır; Order/Return/Procurement business runtime'ı eklenmedi. S2-4'e target dispatch, gerçek terminal hook'u ve source step'in uzak sonucu bekleyerek YAML transition yürütmesi kalır. Dashboard/BigPicture UI ve canlı instance ilişki bildirimi değiştirilmedi.

### S2-3 review: kısmi yürütüm ve erken terminal parking

S2-3-01/02 düzeltmeleri `Implemented`; S2 `Partial`. Request publish → participant command publish sırası korunur. [`SagaStepExecutor`](../src/SagaFlow.Engine/Coordination/SagaStepExecutor.cs) ikinci publish başarısız olduğunda invocation/publication geçmişini silmez. Request attempt'i varsa step'e immutable [`InvocationReconciliation`](../src/SagaFlow.Core/State/InvocationState.cs) (Reason, RequiredAt) kaydeder. Dispatched step Failed, source Saga Failed olur; senkron outcome ile zaten ilerlemiş forward history geriye yazılmaz, reconciliation işareti yine korunur. Bu işaret failure window'un geri alınmış sayılamayacağını bildirir; otomatik compensation/rollback yoktur. [`ProcessInvocationManager.FindAsync`](../src/SagaFlow.Engine/Coordination/ProcessInvocationManager.cs) relationship snapshot'ında Reconciliation alanını döndürür. Sonraki acceptance veya remote terminal Completed bu işareti temizlemez ve source Saga'yı başarılı yapmaz. S2-4, yalnız remote Completed durumunu başarı kabul etmemeli; source/step durumu ve açık reconciliation kaydını da kontrol etmelidir. Manuel uzlaştırma/karar olmadan yerel akış otomatik sürdürülmez. Target state kendi store'unda kalır; duplicate request aynı ID'yi kullanır.

[`RabbitMqInvocationTransport`](../src/SagaFlow.Messaging.RabbitMQ/RabbitMqInvocationTransport.cs), acceptance bekleyen geçerli terminal mesajını aynı source outcome queue adına `.deferred` eklenmiş **durable parking queue**'ya persistent olarak kopyalar. Publisher confirm alınmadan orijinal ack edilmez. Park işlemi/ack belirsizliğinde duplicate mümkündür; aynı MessageId fingerprint dedup state'i korur. Parking yayınlanamazsa orijinal teslim retryable tutulur; erken terminal koşulunun kendisi artık aktif consumer'da sonsuz requeue edilmez. Altyapı bağlantı hataları için mevcut gecikmeli nack politikası ayrı olarak sürer.

Parking queue üzerinde aktif consumer, TTL, otomatik polling veya retry timer yoktur. Başarılı acceptance işlenip ack edildikten sonra en fazla **64** parked mesaj için tek bounded replay geçişi yapılır. Başlangıçtaki ready message sayısı ile çağrının limiti minimum alınır; yeniden park edilen mesaj aynı pass içinde sürekli tüketilmez. NotReady veya henüz bulunamayan source state için mesaj confirm sonrası kuyruğun sonuna taşınır; source state eksikliği recovery iddiası olmadan manuel uzlaştırmaya bırakılır. Correlation, source/target identity ve MessageId doğrulaması replay sırasında tekrar yapılır; geçersiz/çelişen terminal sonuç reject/no-requeue ve log ile reddedilir.

Adapter opt-in olarak [`IProcessInvocationReconciliation`](../src/SagaFlow.Core/Abstractions/IProcessInvocationReconciliation.cs) portunu kaydeder. `GetDeferredCountAsync()` bekleyen mesaj sayısını gösterir; broker yönetiminde queue body/MessageId/InvocationId incelenebilir. `ReconcileDeferredAsync(maxMessages)` explicit operatör/uygulama geçişidir, **1..64** mesaj inceler ve uygulanan sayıyı döndürür. Limit pass başınadır; toplam ömür boyu retry sayacı veya scheduler yoktur. Acceptance hiç gelmezse mesajlar süresiz parking'de kalır; otomatik kaynak tüketen bir retry döngüsü çalışmaz. Backlog 64'ü aşarsa sonraki bounded geçişler explicit olarak çağrılmalıdır. API'nin tekrar çağrılması yeni invocation veya target oluşturmaz.

Broker parking InMemory state değildir: queue durable, mesaj persistent ve transfer confirm'lidir; testte host durduktan sonra da mesajların brokerda kaldığı doğrulandı. Bu bir durable database inbox/outbox, broker felaket kurtarma veya exactly-once garantisi değildir. Source state/dedup InMemory kalır; process restart'ında source state kaybolursa otomatik yeniden kurma/uzlaştırma yoktur. Broker queue'nun operatörce silinmesi veya broker verisinin kaybı bu PoC ile giderilmez. Parking-to-ack geçişinde oluşabilecek duplicate'ler normal identity/fingerprint doğrulamasıyla ele alınır.

Kanıt: [`ProcessInvocationIntegrationTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ProcessInvocationIntegrationTests.cs) gerçek RabbitMQ participant publisher channel'ını kapatıp invocation kanalını açık tutarak Source Failed/Target Pending, korunmuş ilişki ve stable duplicate target'ı sınar. Erken terminal testleri acceptance sonradan geldiğinde uygulamayı, hiç gelmediğinde durable beklemeyi, sıfır parking consumer'ını ve iki mesajda 1/64 limitli replay sayısını koşul/counter üzerinden doğrular; arbitrary sleep kullanılmaz. Çelişen terminal gerçek adapter reject sayacıyla doğrulanır. [`ProcessMessagingTests.PartialExecutionMarkerSurvivesRemoteTerminalSuccess`](../tests/SagaFlow.Engine.Tests/ProcessMessagingTests.cs) remote Completed sonrasında dahi Source Failed ve açık reconciliation kaydını doğrular. Normal acceptance → terminal regresyonu korunur. Güncel koşu sonuçları [proje durumunda](01-PROJECT_STATUS.md).

S2-3-R01 kapanışı: [`ProcessInvocationIntegrationTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ProcessInvocationIntegrationTests.cs) gerçek broker üzerinde `.deferred` kuyruğunu geçici kaldırarak mandatory parking publish/confirm hatasını oluşturur. Orijinal terminal ACK edilmeden NACK/requeue üzerinden tekrar teslim edilir; kuyruk yeniden açılınca tek parked mesaj ve acceptance sonrasında terminal gözlemi doğrulanır. İkinci test, aynı kaynak process altındaki iki bağımsız invocation terminalini park eder: ilk acceptance yalnız ilk state’i, ikinci acceptance yalnız ikinci state’i tamamlar; Message ID ve Invocation ID ayrılığı, kalan queue sayısı ve duplicate terminalde değişmeyen `UpdatedAt` sınanır. Odak koşu 2/2, izole tam suite 458/458; build 0 uyarı/hata. Bu kanıt aynı process/vhost ve InMemory source state sınırındadır; restart recovery veya exactly-once garantisi eklemez.

## S2-4: target execution ve source external-step continuation

Durum: yerel cross-domain koordinasyon entegrasyonu `Implemented`; S2 bütünü `Partial`. Önceki S2-3 bölümlerindeki target Pending/no automatic terminal hook/source transition Planned ifadeleri bu aşamanın aşağıdaki davranışıyla güncellenir. S2-5/S2-6 instance/BigPicture runtime görünürlüğü ve durable recovery bu çalışmada geliştirilmedi.

### External YAML ve uygulama modeli

[`SagaStepDefinition.External`](../src/SagaFlow.Core/Definitions/SagaStepDefinition.cs) geriye uyumlu bool alandır; varsayılan false, canonical YAML'da false atlanır. `external: true` bir participant command göndermek yerine invocation terminal sonucunu bekler. Command hâlâ zorunlu handler etiketi/identifier'dır. `bigPicture.relationship: invokes` tek başına execution başlatmaz. Örnek kaynak tanımı:

```yaml
name: source-process
domain: domain-x
version: "1.0"
steps:
  - name: remote
    command: InvokeRemote
    external: true
    transitions:
      - event: success
        nextStep: finalize
      - event: failure
        nextStep: compensation
  - name: finalize
    command: Finalize
    transitions:
      - event: success
        nextStep: completed
bigPicture:
  - domain: domain-y
    process: remote-process
    version: "1.0"
    endpoint: https://definition-discovery
    relationship: invokes
```

Domain Starter scoped `ILocalSagaStepHandler` içinde `command.Name == "InvokeRemote"` olduğunda `await sagaFlow.InvokeAsync("remote-process", command.Payload, cancellationToken)` çağırır; diğer command'larda handler return etmelidir. Source metadata uygulamadan alınmaz. Handler sonrasında mevcut publisher request'i claim edip gönderir. [`SagaStepExecutor`](../src/SagaFlow.Engine/Coordination/SagaStepExecutor.cs) External step için participant forward publish yapmaz; handler invocation veya enabled transport yoksa açık dispatch hatası üretir. External step Dispatched kalır: bu, mesaj temelli beklemedir; consumer/worker thread'i remote completion Task'i beklemez. InvokeAsync receipt Prepared semantiği değişmedi. External=false legacy step'ten invocation yapılırsa eski fire-and-forget + participant davranışı korunur; uzak terminal yalnız ilişkiyi günceller.

### Target başlangıcı ve ilk dispatch sınırı

[`ProcessInvocationReceiver`](../src/SagaFlow.Engine/Messaging/ProcessInvocationReceiver.cs) önce admission ve atomik GetOrCreateTargetAsync, sonra acceptance publish, ardından [`SagaCoordinator.StartIncomingAsync`](../src/SagaFlow.Engine/Coordination/SagaCoordinator.cs) yürütür. Target kendi ID/correlation değerlerini korur. StartIncomingAsync Local ownership/incoming kaydını kontrol eder ve ilk Pending step'i aynı store callback'inde mevcut executor.Prepare ile Dispatched yapar. Broker I/O callback dışındadır. Eşzamanlı duplicate request/acceptance aynı target kaydını kullanır; ilk step zaten Dispatched/Completed/Failed ise tekrar başlatılmaz.

Acceptance publication başarısızsa target henüz Pending kalabilir; request redelivery aynı instance'ı bulup acceptance'ı tekrar yollar ve ilk dispatch'i claim eder. İlk command publish hatasında mevcut executor target step/Saga'yı Failed yapar, failure reason saklanır ve gerçek terminal Failed bildirimi denenir. Duplicate request yeni instance yaratmaz veya ilk step'i yeniden publish etmez; mevcut failed target ve stable terminal sonucu tekrar bildirilir. İlk dispatch claim'i ile publish arasında durable outbox yoktur: crash/confirmation uncertainty için exactly-once veya otomatik yeniden dispatch garantisi verilmez. Broker request redelivery geçici hataları tekrar işler; terminal Failed target'ın business etkisinin kesinlikle hiç oluşmadığı iddiası değildir, belirsiz durum manuel uzlaştırma gerektirir.

### Genel terminal lifecycle hook'u

Opt-in process adapter'ın scoped ProcessInvocationPublisher servisi Coordinator'a bağlanır. Coordinator target initial dispatch, forward outcome/next dispatch, compensation ve DomainRollback tamamlanması sınırlarından sonra IncomingInvocation sahibi gerçek Completed/Failed/Compensated Saga'yı bildirir. TimedOut/Compensating/CompensationRequested terminal olarak yeniden sınıflandırılmaz. Domain başına publisher yazmak gerekmez. Uygulamanın doğrudan store mutation yapması genel bir terminal event hook'u değildir; explicit PublishTerminalAsync mevcut manuel uzlaştırma yolu olarak kalır.

Target InvocationTerminalOutcome kaydı ilk gerçek terminalden üretilir ve MessageId/identity korunur. Birden fazla hook gözlemi aynı mantıksal mesajı tekrar gönderebilir; source dedup tek step transition sağlar. Notification I/O store callback dışında yapılır. Publish hatası çağırana taşınır, target terminal state geri çevrilmez. Önceki local participant outcome commit edilmişken notification başarısızsa broker outcome redelivery'sindeki DuplicateSagaOutcome yolu notification'ı yeniden dener; explicit PublishTerminalAsync de aynı kaydı kullanır. Compensation ve domain rollback mevcut mekanizmalarıyla çalışır. Incoming business context JsonElement olduğundan target rollback handler gerekiyorsa mevcut SagaRollbackHandler<JsonElement> sözleşmesiyle kaydedilebilir; normal StartAsync typed payload davranışı değişmedi.

### Source completion ve yarış koruması

Receiver önce tam invocation/source/target/correlation ve target Saga kimliğini doğrular, S2-2 ObserveAsync ile ilişki gözlemini kaydeder, ardından Coordinator.HandleExternalTerminalAsync çağırır. İkinci store callback'inde **tekrar** identity/status/target ID, source Running, aynı current Dispatched External step, boş Result ve kapalı Reconciliation kontrol edilir. Gözlem ile continuation arasındaki eşzamanlı local failure bu kontrolü geçemez. Remote gözlem korunur; Failed/TimedOut source veya açık reconciliation yeniden Running/Completed yapılmaz. Manuel uzlaştırma kaydı otomatik temizlenmez.

Completed → success; Failed/Compensated → failure; business Rejected → failure event'i mevcut TransitionResolver/ProcessStateManager'a verilir. YAML uygun success/failure politikalarını tanımlamalıdır. External result JSON'unda `status` (Completed/Failed/Compensated/Rejected), `invocationId`, terminalde `targetSagaInstanceId`, rejection'da `reason` bulunur; uzak business result payload'ı icat edilmez. Structured condition'lar bu teknik sonucu kullanabilir. Failure transition compensation veya recovery step seçebilir; source'un son Saga status'u kendi YAML/rollback politikasının sonucudur, remote failure otomatik source Completed anlamına gelmez.

External step Result/status, source transition ve bir sonraki command'ın Prepare claim'i tek atomik UpdateAsync içindedir. Duplicate terminal aynı step'i ikinci kez complete/dispatch etmez. Participant outcome external step'i tamamlayamaz; explicit local compensation outcome'ları mevcut compensation planı üzerinden işlenir. Dış I/O commit sonrasındadır; sonraki command publish başarısızlığı mevcut Failed/manual-resolution sınırlarını taşır, durable transaction eklenmedi. Observe ve continuation iki ayrı yerel commit'tir; crash recovery/outbox yoktur. Redelivery aynı stable terminal ile devamı yeniden değerlendirebilir; process restart state kaybını onarmaz.

S2-3 parking/replay, metadata/fingerprint kontrolü ve Reconciliation koruması sürer. Erken terminal acceptance sonrasında replay edildiğinde de aynı source continuation yolunu kullanır. Scope cleanup, compensation routing, normal StartAsync ve legacy related/invokes metadata korunur. Static discovery/canonical snapshot External alanını taşır; Dashboard/BigPicture UI'ya external/wait/instance ilişki görünürlüğü eklenmedi.

Kanıt: [`CrossDomainCoordinationTests`](../tests/SagaFlow.Engine.Tests/CrossDomainCoordinationTests.cs) gerçek Engine/store ile target tek-dispatch, success/failure/compensated/rejected devamı, concurrent duplicate, initial/terminal publish failure, stable retry, malformed participant outcome, observation-continuation arası local failure, reconciliation ve YAML roundtrip'i sınar. [`ProcessInvocationIntegrationTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ProcessInvocationIntegrationTests.cs) iki ayrı IHost/container/store/managed RabbitMQ bağlantısında gerçek request → target participant command/outcome → automatic terminal → source participant continuation zincirini, compensation/failure/duplicate ve geç acceptance replay'ini doğrular. Worker test driver'ları brokerdaki command'ları okuyup generic outcome yayımlar; target/source terminal state normal E2E'de doğrudan değiştirilmez. Hostlar aynı test OS process'i içindedir. S2-3 testlerinin eski target Pending beklentileri, receiver sonrası artık Dispatched olacağı için güncellendi; yalnız GetOrCreateTargetAsync çağrısının Pending semantiği korunur.

## S2-5: yerel Instance Dashboard traceability

Source/target ilişki projeksiyonu ve UI `Implemented`; final gerçek Chrome UX kabulü, HTTP invocation izolasyonu ve eksik metadata doğrulaması `PASS` (aşağıdaki final kabul kaydı). Önceki Computer Use engeli tarihsel doğrulama kaydında korunur. S2 `Partial`; S2-6 BigPicture runtime ilişkileri, federated instance sorgusu veya recovery geliştirilmedi. Önceki aşamalardaki instance görünürlüğü Planned ifadeleri bu yerel UI/API kapsamı için güncellenmiştir.

### Yerel API ve alan semantiği

[`SagaFlowDashboardEndpoints`](../src/SagaFlow.Hosting/Endpoints/SagaFlowDashboardEndpoints.cs) mevcut GET instance list/detail yanıtlarını additive alanlarla genişletir. Liste `origin` (Local/External), `definitionDomain`, `definitionVersion` sunar. Detail `origin`, nullable `incomingInvocation`; step detail `external` ve nullable `invocation` sunar. Her endpoint yalnız hostun ISagaStateStore kayıtlarını okur; discovery registry'sinden remote instance üretmez ve remote state fetch etmez. Origin IncomingInvocation varlığından türetilir, ikinci ilişki kaydı saklanmaz.

Step invocation projeksiyonu: invocationId, target {domain,process,version}, nullable targetSagaInstanceId, nullable lastKnownStatus, createdAt, updatedAt, lastObservedAt ve reconciliation {reason,requiredAt}. Progress bulunmayan eski/eksik kayıtta bilinmeyen status/timestamp null'dır; başarı uydurulmaz. Bunlar kaynak domainin yerel kaydıdır: CreatedAt ilk kayıt, UpdatedAt son external status değişimi, LastObservedAt son yerel gözlem zamanıdır. Aynı status veya duplicate mesaj UpdatedAt'ı yeni bir remote transition gibi göstermez. LastKnownStatus uzak domainin şu anki otoriter state'i değildir; target Running olsa bile source son olarak Accepted gözlemlemiş olabilir. S2-5 yeni Running propagation mesajı veya execution politikası eklemez.

Incoming projeksiyon: invocationId, source {domain,process,version}, sourceSagaInstanceId, sourceStep, sourceCorrelationId, sourceRequestCreatedAt ve acceptedAt. Target'ın kendi id/correlationId alanları root detail'de kalır. SourceRequestCreatedAt IncomingInvocation.CreatedAt'tır ve source mesaj oluşturulma zamanıdır; target received timestamp diye etiketlenmez. Ayrı receipt timestamp state'te kayıtlı değildir; acceptedAt hedefteki bağlama/kabul zamanıdır. UI bunları Source request created / Accepted locally olarak sunar.

Yeni ilişki DTO'ları allowlist'tir: request payload, broker routing/connection options, gRPC endpoint, publish attempt/error ayrıntıları ve observed-message fingerprint ledger'ı gönderilmez. Mevcut step Input/Result, FailureReason ve host'un DescribeDomainStarter alanları korunur; bunlar business verisi veya uygulama exception metni içerebilir. Bu çalışma mevcut payload/error redaction veya erişim güvenliği sağlamaz. Dashboard/API/SignalR erişim sınırı host/ağ sorumluluğundadır; production-ready auth/secrecy iddiası yoktur.

### UI, kimlik kopyalama ve reconciliation

[`InstanceTraceability`](../src/SagaFlow.Dashboard.Client/src/components/InstanceTraceability.tsx) ve [`InstancesPage`](../src/SagaFlow.Dashboard.Client/src/pages/InstancesPage.tsx) mevcut liste ve execution-flow düzenini korur. Liste/detail Origin badge içerir. External step mor etiket ve ayrı ilişki alanıyla participant step'ten ayrılır. Local Saga status, local step status ve last known external status ayrı alanlardır. Dispatched external step bekleme olarak açıklanır; terminal gözlem kaydedilmiş fakat local continuation henüz gerçekleşmemişse ayrı pending açıklaması gösterilir. Accepted tamamlanma anlamına gelmez.

Target detail Incoming origin panelinde kaynak süreç/step/Saga/correlation ve kendi target-local Saga/correlation kimlikleri ayrı etiketlidir. Güvenilir remote Dashboard adresi mevcut konfigürasyonda yoktur; gRPC discovery origin'inden sahte Dashboard URL üretilmez. Remote ID'ler yalnız selectable text/kopyalama düğmesiyle sunulur. Clipboard kullanılamazsa manuel seçip kopyalama mesajı gösterilir. Yeni host registry, URL resolver veya uzaktan instance navigation protokolü eklenmedi.

Reconciliation uyarısı local step, invocation, reason, RequiredAt ve son bilinen external status'u gösterir. Uyarı step detayları kapatılsa bile görünür kalır. Source Failed görünümü ve açık reconciliation, remote Completed gözlemiyle gizlenmez/temizlenmez. Recovery/manuel aksiyon UI'sı yoktur. Detaylar mevcut inline expand/collapse alanındadır; kalıcı drawer/split-panel genişliği eklenmedi. Kimlik metinleri break-all/min-w-0, header'lar wrap ve grid'ler responsive; mevcut dark/light sınıfları kullanılır. Final Chrome kabulünde light/dark ve 390px ekran görüntüleri incelendi; yatay overflow assertion'ları geçti.

### SignalR ve doğrulama sınırları

[`ProcessInvocationManager`](../src/SagaFlow.Engine/Coordination/ProcessInvocationManager.cs) prepare/observe/get-or-create/attach commit sonrasında, [`ProcessInvocationPublisher`](../src/SagaFlow.Engine/Messaging/ProcessInvocationPublisher.cs) Requested/publication bookkeeping sonrasında mevcut SagaStateChangePublisher'ı kullanır. Target ilk dispatch claim'i de aynı bildirimi üretir. Callback içinde network I/O veya ikinci notification sistemi eklenmedi; best-effort listener hataları Saga semantiğini değiştirmez. Event yalnız yerel InstanceId taşır; istemci mevcut API'den liste/seçili detail'i yeniden okur. Duplicate observation aynı yerel state'e tutarlı refresh üretebilir.

[`useSagaFlowLiveUpdates`](../src/SagaFlow.Dashboard.Client/src/live/useSagaFlowLiveUpdates.ts) hub URL'sini document.baseURI üzerinden API istemcisiyle aynı tabandan çözer; /instances gibi alt URL'ye yanlış /hub ekleme sorunu giderildi. Mevcut reconnect callback'i tüm yerel görünümü yeniler, manual Refresh korunur. Transport disconnect retry/cleanup modeli değiştirilmedi; remote hub'a abonelik/federation yoktur. Geç gelen manual refresh yanıtı kapatılmış detail'i yeniden açmaz; başarılı live refresh eski hata metnini temizler.

Kanıt: [`InstanceTraceabilityApiTests`](../tests/SagaFlow.Hosting.Tests/InstanceTraceabilityApiTests.cs) gerçek Engine/store ve HTTP/SignalR üzerinden Prepared/Requested/Accepted/Running/Completed, nullable target, incoming correlation, tarih, duplicate ve reconciliation projeksiyonunu sınar. [`ProcessInvocationIntegrationTests.DashboardApisProjectBothSidesOfActualCrossDomainExecution`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ProcessInvocationIntegrationTests.cs) iki ayrı Kestrel Domain Starter + gerçek RabbitMQ request/acceptance/target outcome/terminal/source continuation ile her API'nin yalnız kendi instance'ını gösterdiğini doğrular. [`instanceTraceability.test.tsx`](../src/SagaFlow.Dashboard.Client/tests/instanceTraceability.test.tsx) sekiz external status, origin, copy, reconciliation, expand/collapse ve legacy dark DOM davranışını; [`instanceLiveUpdates.test.tsx`](../src/SagaFlow.Dashboard.Client/tests/instanceLiveUpdates.test.tsx) base-path/reconnect/latest callback/cleanup davranışını sınar.

InMemory state/observation/dedup restart'ta kaybolur; SignalR bildirimi state persistence veya event history değildir. Yeniden bağlanma yalnız mevcut yerel state'i yeniden okur, kaybolan state'i geri getirmez. S2-6 BigPicture runtime ve merkezi/federated trace bu UI ile uygulanmış sayılmaz.

### S2-5 final UX ve entegrasyon kabulü

Son ek browser assertion'ları normal invocation ve eksik optional metadata için reconciliation alert bulunmadığını da kontrol eder. Bu assertion'lardan sonra izole odak yeniden **1/1** geçti, browser sonucu yine **PASS**: `.artifacts/s21-review/s25-acceptance-final-assertions.log`. Odak filtresi nedeniyle diğer dört test assembly'sinde eşleşme bulunmaması beklenir; bunların tam koşu sonucu aşağıdaki 478/478 kaydıdır.

2026-11-04: [`InstanceUxAcceptanceTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/InstanceUxAcceptanceTests.cs), iki ayrı IHost/container/state store/managed RabbitMQ bağlantısında iki bağımsız invocation üretir. Testler gerçek Kestrel Dashboard HTTP API yanıtlarını kullanır; invocation ID, source/target Saga ID, iki correlation ve her iki process identity eşleşmesini karşılaştırır. İlk invocation gerçek target participant outcome ve otomatik terminal bildirimiyle tamamlanınca ikinci invocation'ın **source ve target API JSON'ları birebir aynı** kalır. DTO mapping testi veya mock API bu izolasyonun kanıtı olarak kullanılmaz.

Üçüncü invocation, test-only local handler ile InvokeAsync sonrasında publish öncesinde bekletilir: gerçek Prepared kaydında target Saga ID ve lastObservedAt null, local Saga incomingInvocation null, normal invocation reconciliation null'dır. HTTP assertion'ları ve browser bu eksik alanları doğrular; UI bilinmeyen ID için `Not known yet`, timestamp için `Not recorded` gösterir, sahte target link/copy kimliği üretmez. Reconciliation ekranı ek generic source hostunda yalnız participant publisher channel kapatılarak gerçek dispatch failure ile üretilir; invocation RabbitMQ kanalı aktiftir. Saga state elle terminal veya reconciliation durumuna çevrilmez.

[`verify-instance-ux.mjs`](../scripts/verify-instance-ux.mjs) gerçek loopback hostları Chrome **156.0.8078.80** ile headless çalıştırır. Source external, target incoming, ayrı correlation, last-known açıklaması, reconciliation, null metadata, inline expand/collapse, mevcut workflow Drawer aç/kapat, gerçek clipboard, light/dark ve 390×844 görünümü geçti. Gerçek RabbitMQ completion sırasında browser ana frame navigation sayısı sıfır kalırken SignalR ile Completed görüldü. Page error yok; dokuz ekran görüntüsü görsel olarak kontrol edildi. Browser UX **PASS**, Multiple Invocation Isolation **PASS**, Missing Metadata **PASS**. Bunlar sample Order/Return business E2E veya diğer tarayıcıların kabulü anlamına gelmez.

Tekrar çalıştırma (repo kökünde, mevcut Chrome ve Docker/RabbitMQ ile):

```powershell
npm install --prefix .artifacts/s25-browser playwright@1.64.1 --no-audit --no-fund
npm --prefix src/SagaFlow.Dashboard.Client run build
$env:SAGAFLOW_BROWSER_ACCEPTANCE='true'
$env:SAGAFLOW_RABBITMQ_USERNAME='sagaflow'
$env:SAGAFLOW_RABBITMQ_PASSWORD='sagaflow-dev'
./scripts/run-rabbitmq-integration.ps1
```

Playwright yalnız ignored test çıktı dizinine kurulur; ürün bağımlılığı/framework eklenmez. Browser bayrağı kapalıysa yeni test HTTP izolasyon/metadata bölümünü çalıştırır; browser bölümü çalışmış sayılmaz. Kanıtlar ignored `.artifacts/s25-acceptance/01-source-light.png` … `09-workflow-drawer.png`, `browser-result.json`, `isolation.json`; manifest host URL'leri test ömrüyle sınırlıdır. İlk odak build missing RabbitMQ extension using nedeniyle durdu; using düzeltildikten sonra yeni odak **1/1** geçti. Final browser bayrağı açık tam runner **478/478**, sıfır skip/failure; full .NET build sıfır warning/error. Dashboard suite **72/72**, TypeScript `tsc -b` ve Vite build başarılı; mevcut SignalR dependency Rolldown annotation uyarısı sürer. Ayrıntılı log yolları [proje durumunda](01-PROJECT_STATUS.md) kayıtlıdır. Final görevde ürün kodu düzeltmesi gerekmedi; state/invocation/execution/RabbitMQ sözleşmeleri korundu. S2-5 bu kabul kapsamıyla kapatılabilir; S2/S6 `Partial` kalır.
