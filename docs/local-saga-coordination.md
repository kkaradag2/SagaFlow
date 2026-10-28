# Local Saga Coordination ve InMemory instance yönetimi

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
