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
