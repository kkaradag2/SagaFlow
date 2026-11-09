# Local Saga Coordination ve InMemory instance yönetimi

## S2-7A: target application acceptance ve business materialization

`Implemented`: [`ProcurementOrderStep`](../samples/ProcurementProcess/SagaFlow.Procurement/ProcurementService.cs), kendi YAML `external: true` adımında yalnız business payload ile `InvokeAsync("order-process", payload)` çağırır. `invokes` tanımı izin/keşif ilişkisidir; kendi başına invocation üretmez. Source Saga/step, identity ve correlation Engine tarafından türetilir. Onay reddi yerel `Completed`/business `Rejected`; Order Completed → yerel `Completed`/`Fulfilled`; Order Failed veya Compensated → YAML failure transition → yerel `Compensated`/business `Failed`. Procurement için telafi edilecek kaynak iş adımı veya sahte rollback eklenmedi.

Mevcut [`IProcessInvocationPolicy`](../src/SagaFlow.Engine/Messaging/ProcessInvocationReceiver.cs), teknik hedef ownership/version doğrulamasından sonra saf business admission çalıştırır. [`IncomingOrderService`](../samples/OrderProcess/SagaFlow.Order/Services/IncomingOrderService.cs) yalnız procurement/procurement-process@1.0 kaynağı, nonempty PurchaseRequestId, mevcut Order customer/item/quantity/price/FailureAt kuralları ve hesaplanabilir toplamı kabul eder. Business rejection target Saga/Order oluşturmadan sticky Rejected cevabıdır; beklenmeyen policy exception'ı teknik retry hatasıdır, business rejection gibi kaydedilmez. Kaynak filtresi sample kuralıdır; authentication değildir.

Yeni opsiyonel [`IIncomingInvocationInitializer`](../src/SagaFlow.Engine/Messaging/IIncomingInvocationInitializer.cs), başarılı admission ve atomik target claim **sonrasında**, Accepted yayını ve ilk dispatch **öncesinde** çalışır. Order bu hook'ta kendi OrderId'sini/kaydını üretir; doğrudan POST /orders ile aynı [`OrderCreation`](../samples/OrderProcess/SagaFlow.Order/Services/OrderCreation.cs) doğrulama/normalizasyon/toplam yolunu kullanır. Engine'e Order tipi veya business kuralı eklenmez. Hook kayıtlı değilse önceki ham-JSON başlangıcı sürer. İlk dispatch sonrası duplicate initializer'ı yeniden çalıştırmaz; eşzamanlı ilk teslimlerde hook tekrarlanabilir. Uygulama aynı invocation/target için aynı payload ve idempotent business etki sağlamalıdır.

[`ProcessInvocationManager`](../src/SagaFlow.Engine/Coordination/ProcessInvocationManager.cs) yalnız yerel Running/all-Pending target context'ini atomik günceller, original `IncomingInvocation.Payload` ve kimliği değiştirmez. Callback içinde business/broker I/O yoktur. Order'ın kilitli InMemory invocation→Order eşlemesi eşzamanlı duplicate için tek kayıt ve sabit başlangıç payload'ı sağlar; GET ilişki kaydı mevcut yerel Order durumunu okur. Hook exception'ı Pending target bırakır, Accepted/ilk dispatch gönderilmez; redelivery aynı claim üzerinden tekrar dener. Accepted, Order tamamlandı demek değildir. Order'ın gelen JSON context rollback adapter'ı mevcut CancelAsync yolunu kullanır; normal typed Order yürütümü ve Payment/Inventory/Shipment/Notification YAML akışı korunur.

Business repository, Saga store ve RabbitMQ arasında transaction yoktur. Business yazımı sonrası crash, kaybolan confirm, restart'ta eşleme/state kaybı ve orphan kayıt riski sürer. Tek InMemory store claim garantisi business kayıtları için genel exactly-once değildir; durable inbox/outbox/recovery eklenmedi. PurchaseRequestId, OrderId, iki SagaInstanceId, InvocationId ve iki correlation ayrıdır. Procurement yalnız kendi state/store'unu okur. Terminal sözleşmesi business OrderId taşımaz; kimlik Order'ın `/orders/invocations/{invocationId}` endpoint'inde bulunur, kaynak tarafa uydurulmaz veya senkron HTTP ile çekilmez.

Kanıt: [`IncomingInvocationInitializationTests`](../tests/SagaFlow.Engine.Tests/IncomingInvocationInitializationTests.cs) original request ayrımı, policy rejection, initializer retry ve concurrent initial delivery; [`ProcurementDomainTests`](../tests/SagaFlow.Samples.Tests/ProcurementDomainTests.cs) API validasyon/rejection, pre-invocation dispatch failure ve Order idempotency; [`ProcurementRabbitMqTests`](../tests/SagaFlow.Samples.Tests/ProcurementRabbitMqTests.cs) gerçek broker/participant worker'ları, ayrı store'lar, HTTP Dashboard/relationship API ve gRPC discovery kullanır. Acceptance/terminal ters sıralama ve parking mevcut [`ProcessInvocationIntegrationTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ProcessInvocationIntegrationTests.cs) kapsamındadır. Güncel sonuçlar [doğrulama kaydında](01-PROJECT_STATUS.md). Testte iki ana uygulama aynı OS process'indedir; ayrı OS process deneysel kanıtı S2-7B `Planned`, S2 `Partial`.

> Tarihsel ilk PoC kapsamı: transport, compensation yürütümü ve dashboard hakkındaki aşağıdaki yokluk ifadeleri bu geliştirme aşamasını anlatır. Güncel uygulama ve test durumu için [01-PROJECT_STATUS.md](01-PROJECT_STATUS.md) esas alınır.

Bu geliştirme, Domain Starter içine gömülü ilk çalışan ileri Saga yürütümünü sağlar. YAML yükleme/validation, Registry ve gRPC tanım keşfi korunur. Mesaj göndermek için yalnızca `ISagaMessageBus` portu kullanılır; kütüphanede varsayılan transport veya InMemory Message Bus yoktur.

## BPI-1A: dil bağımsız participant wire sözleşmesi

Durum: `Implemented` (participant command/outcome transport'u). Test kapsamı ve gerçek koşu sayıları [proje durumunda](01-PROJECT_STATUS.md#bpi-1a-dogrulama) tutulur. Kaynaklar: [`SagaMessageHeaders`](../src/SagaFlow.Core/Messages/SagaMessageHeaders.cs), [`ExecutionMetadata`](../src/SagaFlow.Engine/Messaging/ExecutionMetadata.cs), [`RabbitMqMessageSerializer`](../src/SagaFlow.Messaging.RabbitMQ/RabbitMqMessageSerializer.cs), [`RabbitMqTransport`](../src/SagaFlow.Messaging.RabbitMQ/RabbitMqTransport.cs).

### Body ve properties sınırı

Command body tam olarak `SagaCommand.Payload`, outcome body tam olarak `SagaOutcomeEvent.Result` JSON değeridir. `name`, `payload`, `result`, `headers`, `isSuccess` gibi teknik envelope alanları eklenmez. Uygulamanın kendi şemasında aynı adlı alanlar bulunabilir; alıcı body şekline bakarak format tahmin etmez. Command ve result farklı nesne, dizi, primitive veya null JSON şemaları olabilir. Engine hâlâ her forward/compensation command'a mevcut başlangıç `Context` değerini verir; yeni payload mapping/merge mekanizması eklenmedi. Sonuçlar ilgili step'in `Result` alanındadır. [`RabbitMqWireContractTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/RabbitMqWireContractTests.cs) iki farklı business şemasıyla aynı iki-adımlı akışı ve bağımsız result şemalarını gerçek brokerda inspect eder.

[AMQP properties ve headers](https://www.rabbitmq.com/docs/publishers) adapter sınırında kullanılır; Core'da RabbitMQ tipi yoktur:

| AMQP property | Command | Outcome |
|---|---|---|
| `ContentType` | `application/json` | `application/json` |
| `ContentEncoding` | `utf-8` | sample publisher `utf-8`; reader JSON/UTF-8 kabul eder |
| `Type` | business command adı | participant'ın nonempty business result/event adı; command Type'ını echo etme zorunluluğu yok |
| `MessageId` | state'teki `CommandId` | participant'ın nonempty mesaj kimliği; command kimliğinden farklı olabilir, UUID algoritması zorunlu değil |
| `CorrelationId` | immutable execution correlation ile aynı | aynı execution correlation ile aynı |
| `ReplyTo` | local `{domain}.outcomes` queue | echo zorunlu değil; outcome bu adrese default exchange üzerinden gönderilir |
| `Expiration` | varsa YAML timeout'un milisaniye TTL'i | echo zorunlu değil |
| `Timestamp` | adapter yayın zamanı (Unix saniyesi) | participant'ın yayın zamanı olabilir; execution başlangıcı yerine kullanılmaz |

Command exchange mevcut direct `sagaflow.commands` (options ile değişebilir), routing key YAML `StepId`'dir; `Type` routing key değildir. Outcome default exchange ile command `ReplyTo` queue'suna gönderilir. Adapter persistent/mandatory/confirm davranışını korur. `ISagaMessageBus.ReplyDestination` opsiyonel transport-independent portuyla Engine, dispatch claim sırasında gerçek reply destination'ı state'e bağlar. Test transport'unda null olabilir; RabbitMQ wire context'inde zorunludur.

### Immutable execution headers ve mesaj alanları

Aşağıdaki değerlerin tamamı AMQP **UTF-8 string/long-string** değerleridir; integer, Boolean veya JSON-serialized header nesnesi değildir. RabbitMQ.Client okurken `byte[]` verebilir. Participant `sf-version` ile bütün `sf-ex-*` değerlerini opaque olarak **değiştirmeden** korur; alanları yeniden üretmez. Core'da bu transport-independent context immutable `SagaMessageHeaders` kaydıdır.

| Header | Sahiplik ve anlam |
|---|---|
| `sf-version` | Zorunlu string `1`; participant wire sürümü |
| `sf-ex-saga-id` | Local Saga instance kimliği |
| `sf-ex-correlation-id` | Local Saga correlation; AMQP CorrelationId ile tutarlı |
| `sf-ex-domain`, `sf-ex-process`, `sf-ex-process-version` | Yerel tanımın tam kimliği |
| `sf-ex-step-id` | YAML step adı; execution kimliği değildir |
| `sf-ex-execution-id` | Bu dispatch claim'inin Engine tarafından oluşturulan UUID'si |
| `sf-ex-command-id` | Bu dispatch'in command UUID'si; command AMQP MessageId ile aynı |
| `sf-ex-command-type` | Orijinal command adı; outcome Type'ı değişse bile korunur |
| `sf-ex-kind` | Yalnız `forward` veya `compensation` |
| `sf-ex-created-at` | Dispatch claim zamanı, UTC ISO-8601 round-trip `O` formatı (`+00:00`, kesirli saniye dahil) |
| `sf-ex-reply-to` | Engine'in beklediği reply destination; command ReplyTo ile aynı |
| `sf-ex-cause-id` | İlk command'da yok; sonraki dispatch'te onu tetikleyen outcome MessageId; outcome'a aynen echo edilir |
| `sf-ex-forward-execution-id` | Compensation'da zorunlu; telafi edilen completed forward execution'a bağlar; normal forward'da yok |

Mesaja özgü iki ek header, immutable execution context'inden ayrıdır:

- `sf-causation-id`: outcome'da **zorunlu**, orijinal command'ın `MessageId`/`sf-ex-command-id` değeri. Command'da önceki outcome varsa onun MessageId'si; ilk command'da yok.
- `sf-outcome`: yalnız outcome'da zorunlu string `success` veya `failure`. `IsSuccess` bu alandan okunur; body'deki bir Boolean/alan adına bağlı değildir. Örneğin `{ "code": "DECLINED" }` body ile `failure` gönderilebilir. `approved: false` ve `accepted: false` ise sample business kararlarıdır, teknik `success` olarak kalır; YAML business dalını seçer.

Participant tüm AMQP properties'yi kopyalamaz. ContentType, kendi Type/MessageId'si, correlation, kopyalanmış execution context'i ve iki outcome header'ını yayınlar. Broker/istemcinin eklediği tracing/publish-sequence header'ları execution context'i değildir; kopyalamak zorunlu değildir. Bütün header'ları körlemesine immutable sayan assertion özellikle kullanılmaz.

### Execution lifecycle, trust ve atomiklik

[`SagaStepExecutor.Prepare`](../src/SagaFlow.Engine/Coordination/SagaStepExecutor.cs), Pending → Dispatched atomik claim'i içinde ExecutionId/CommandId/context üretir ve `SagaStepState.Execution` alanına yazar. Sonraki step yeni kimlik alır. [`CompensationManager`](../src/SagaFlow.Engine/Consistency/CompensationManager.cs), Planned → Dispatched sırasında ayrı `SagaCompensationState.Execution` üretir; forward execution'a referansı saklar. Snapshot/JSON round-trip context'i korur. Bunlar retry/attempt numarası değildir: aynı step'in ikinci dispatch'i zaten reddedilir; `maxAttempts` için yeni lifecycle uygulanmadı.

Wire reader sürüm, zorunlu UTF-8 header türleri, execution kind/zaman, message properties, outcome sonucu ve command causation'ını doğrular. Bilinmeyen `sf-ex-*` alanı v1'de reddedilir. Outcome reply destination local transport sahibine ait olmalıdır. Coordinator, **aynı atomik store callback'i içinde** Saga'nın varlığını, correlation'ı, yerel process/version, bütün kayıtlı execution context'ini, active step/status ve command kimliğini kontrol eder. Compensation aynı doğrulamayı kendi kayıtlı context'iyle yapar. Execution context'i olmayan tarihsel/sentetik in-process state'e v1 wire sonucu uygulanamaz; yalnız test/eski in-process API için sürümsüz context uyumu korunur. Yeni Engine dispatch'leri daima tam context taşır.

`sf-version=1` mesajında yanlış veya çelişkili execution metadata state/revision/dispatch değiştirmez, mevcut reject/no-requeue davranışı korunur. Eksik/desteklenmeyen wire sürümleri aşağıdaki quarantine yolunu kullanır. İlk geçerli outcome result + YAML transition + sonraki claim'i atomik uygular; I/O kilit dışındadır. Kayıtlı tamamlanmış execution'a ait tekrar ACK edilir; ikinci YAML transition, command veya `InvokeAsync` oluşmaz. Bu duplicate kararı outcome MessageId'sinden bağımsızdır; aynı execution için ilk kabul edilen sonuç kazanır. Timeout sonrası late outcome'un mevcut NACK/requeue davranışı korunur. Yayın hatasından sonraki geçerli fakat geç outcome için mevcut manuel çözüm/requeue davranışı değiştirilmedi. Participant outcome'u external invocation step'ini tamamlayamaz.

Bu kontrol kimlik doğrulama/authorization değildir; broker erişim denetimi gereksinimini ortadan kaldırmaz. Payload aynı execution için ilk geçerli outcome'da kabul edilir; durable content fingerprint/inbox eklenmedi. Kanıt: [`ExecutionMetadataTests`](../tests/SagaFlow.Engine.Tests/ExecutionMetadataTests.cs), [`RabbitMqWireContractTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/RabbitMqWireContractTests.cs), mevcut duplicate/timeout/publication-failure transport testleri ve [`ProcurementRabbitMqTests`](../tests/SagaFlow.Samples.Tests/ProcurementRabbitMqTests.cs) içindeki gerçek duplicate approval outcome → tek invocation/target kontrolü.

### Dil bağımsız participant ve deferred sonuç

Uygulayıcının adımları: business JSON body'yi oku; `sf-version`/`sf-ex-*` ve ReplyTo'yu opaque transport context olarak sakla; business işlemi yap; kendi result JSON'unu oluştur; kopyalanmış execution context'ine `sf-outcome` ve `sf-causation-id = command.MessageId` ekle; sonucu orijinal ReplyTo'ya gönder. ExecutionId, SagaInstanceId veya command identity üretim algoritması gerekmez; SagaFlow paketi/SDK'sı kullanılmaz.

Altı .NET sample yalnız sample-local [`SagaFlow.SampleParticipant.Messaging`](../samples/Participants/Shared/SagaFlow.SampleParticipant.Messaging.csproj) projesine ve RabbitMQ.Client'a bağlıdır. Bu helper coordinator, Saga store, YAML transition veya Saga outcome dedup içermez. [`ParticipantProtocol`](../samples/Participants/Shared/ParticipantProtocol.cs) AMQP alanlarını okur/echo eder; normal business servisleri yalnız iş girdileri/sonuçlarıyla çalışır.

**BPI-1A-01 düzeltmesi:** [`ApprovalRecord`](../samples/Participants/SagaFlow.ManagerApproval/Domain/ApprovalRecord.cs) yalnız ApprovalId, OrderId, miktar, zaman/karar ve mevcut publication durumunu; [`InspectionRecord`](../samples/Participants/SagaFlow.Warehouse/WarehouseRepository.cs) InspectionId, ReturnId, OrderId, zaman/karar ve publication durumunu tutar. Bu kayıtlarda SagaInstanceId, StepId, CorrelationId, ReplyTo, ParticipantContext veya transport handle bulunmaz. Teknik alanları `[JsonIgnore]` ile gizlemek yerine modellerden çıkardık. Business repository'leri ApprovalId/InspectionId ile anahtarlanır; aynı OrderId'nin birden fazla approval'ı için mevcut 409, ReturnId'nin başka inspection'a bağlanmaması ve karar/publication uncertainty politikaları korunur.

[`DeferredReplies`](../samples/Participants/Shared/DeferredReplies.cs) yalnız participant'ın transport altyapısında immutable/byte-copy execution context, orijinal ReplyTo ve business-record → `DeferredReplyHandle` eşlemesini tutar. Messaging handler komutu tek parça kaydeder; teknik kimlikleri tek tek kopyalamaz/yeniden üretmez. Decision service publisher'a yalnız business record ID ve kararı verir. Concrete RabbitMQ publisher opaque handle'ı çözerek orijinal context ile outcome'u hazırlar. Aynı execution için farklı context/ReplyTo/business input transport conflict'idir; redelivery business kaydı sıfırlamaz. Bu, sample'ın kabul edilmiş komut/karar eşlemesidir; Engine'in outcome dedup veya coordination sorumluluğunu kopyalamaz. API yolları değişmez; business yanıtlarından eski teknik projeksiyon alanları kaldırıldı, ApprovalId/InspectionId business kimlikleri sunulur.

[`DeferredWireContractTests`](../tests/SagaFlow.Samples.Tests/DeferredWireContractTests.cs) her iki gerçek worker'a ikişer bağımsız context gönderir; business API modelinde teknik alan bulunmadığını, acceptance sonrası boş reply queue'larını, ters sırada verilen kararları, dört ayrı orijinal destination'ı, business-only result'ları ve execution header echo'sunu inspect eder. Derlenmiş yedi sample/helper assembly'nin Core/Engine/Hosting/adapter bağımlılığı olmadığı [`ParticipantDomainTests`](../tests/SagaFlow.Samples.Tests/ParticipantDomainTests.cs) ile kontrol edilir. Başka dilde ayrı runtime participant çalıştırılmadı; dil bağımsızlık kanıtı wire sözleşmesi ve SDK'sız AMQP driver/sample bağımsızlığıdır.

### Legacy geçişi ve cross-domain sınırı

Eski participant formatı sürümsüz JSON `SagaCommand { name, payload, timeToLive, headers }` / `SagaOutcomeEvent { name, isSuccess, result, headers }` envelope'u ve `Type=SagaCommand/SagaOutcomeEvent` idi. Writer **yalnız v1 business-only body** üretir. Reader sürümü yalnız `sf-version` header'ından belirler; body heuristic'i veya compatibility reader yoktur. **BPI-1A-02**, eski reject/no-requeue politikasını eksik/desteklenmeyen wire sürümleri için aşağıdaki quarantine transferiyle değiştirir; legacy business sonucu yeni Saga'ya uygulanmaz.

#### BPI-1A-02: quarantine ve operasyonel geçiş

[`RabbitMqQuarantine`](../src/SagaFlow.Messaging.RabbitMQ/Protocol/RabbitMqQuarantine.cs) yalnız AMQP altyapısıdır; sample helper'a source-link ile derlenir, participant'a SagaFlow runtime/SDK referansı eklemez. Starter outcome consumer'ı `{domain}.outcomes.quarantine`, her sample command queue'su `{stepQueue}.quarantine` adlı **durable, nonexclusive, autoDelete=false** kuyruğu başlangıçta bildirir. Mevcut command/outcome queue adları/argümanları değiştirilmez; eski queue'ya DLX argümanı ekleyerek redeclare hatası yaratılmaz. Quarantine'de consumer, TTL, scheduler veya otomatik replay yoktur.

1. `sf-version` eksikse, `1` dışında/boşsa veya string/UTF-8 değilse body parse edilmeden quarantine seçilir. Legacy command ve outcome aynı kurala tabidir. `sf-version=1` içindeki malformed metadata/business JSON için mevcut reject/no-requeue politikası korunur; bu mekanizma genel malformed-message inbox değildir.
2. Body byte'ları, orijinal MessageId/Type/CorrelationId/ReplyTo ve header'lar kopyalanır. Kopya persistent yayımlanır. Orijinal `Expiration` hedef kopyada temizlenir ve `sf-quarantine-original-expiration` header'ında saklanır; aksi halde legacy command'ın TTL'i quarantine'de de mesajı silebilirdi. `sf-quarantine-source`, `sf-quarantine-exchange`, `sf-quarantine-routing-key`, `sf-quarantine-reason` manuel inceleme için eklenir. Queue/broker retention politikaları ayrıca geçerlidir.
3. Default exchange üzerinden hedef quarantine queue'ya **mandatory=true**, publisher confirms **ve confirmation tracking açık** kanalda publish edilir. Hedef yoksa basic.return/PublishException başarısızlık sayılır. Yalnız bu awaited publish başarıyla bittikten sonra orijinal delivery **tekil ACK** alır. Kuyruğun yalnız konfigürasyonda bulunmasına güvenilmez. [RabbitMQ confirm/ACK ayrımı](https://www.rabbitmq.com/docs/confirms).
4. Transfer/ACK hatasında orijinal ACK edilmez. Outcome adapter'ı tüketimi iptal eder ve transport shutdown'ını callback dışındaki gözlenen task'ta tamamlar; sample worker hata sinyaliyle ExecuteAsync'ten çıkar, channel/connection scope'unu callback dışında kapatır. Kapatma unacked kaynak mesajlarını broker'a döndürür. Böylece aktif NACK/requeue hot loop kurulmaz. Kendi dispatcher callback'inin içinden await channel shutdown yapmak self-wait oluşturabildiğinden kullanılmaz. Background shutdown task'ı dispose sırasında gözlenir. Operatör quarantine topology/izin/bağlantıyı düzeltip host/worker'ı yeniden başlatmalıdır; otomatik retry/restart framework'ü eklenmedi. Host HTTP process'inin açık kalması messaging'in sağlıklı olduğu anlamına gelmez; critical log/consumer sayısı izlenmelidir.
5. Broker confirm sonrası source ACK'den önce kapanma veya confirm/ACK belirsizliği **birden çok quarantine kopyası** üretebilir. Orijinal MessageId/body korunur; bunlar manuel uzlaştırmada karşılaştırılır. Yeni dedup ledger yoktur; MessageId zaten yoksa otomatik tekilleştirme iddiası yapılmaz. Birden fazla kopya Saga state'ine otomatik uygulanmaz. Quarantine at-least-once koruma yoludur, exactly-once transfer veya inbox değildir.

Deployment: mümkünse eski sürümde in-flight/deferred işleri tamamla, quarantine kuyruğu/izinleri/broker dayanıklılığını hazırla, uyumlu producer/consumer'ları birlikte güncelle. Quarantine sayısı ve critical transfer loglarını kontrol et; ham body + properties ile eski işi incele, eski business sonucunu yeni execution'a körlemesine republish etme. Manuel replay dönüştürücüsü veya yeni REST API yoktur. Mixed-version çalışma eski işin devamını sağlamaz; yalnız uyumsuz mesajı ayırır. InMemory context/Saga kaybı header'lardan kurtarılamaz. Kaynak mesajın kendi TTL'i veya broker max-length/TTL politikası **transferden önce** geçerlidir; consumer dururken bu politikalarla silinmeye karşı recovery garantisi verilmez. Quarantine'nin broker/disk kaybı veya yönetimsel silinmesi de bu görevin sınırıdır.

Kanıt: [`RabbitMqReviewRemediationTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/RabbitMqReviewRemediationTests.cs) gerçek consumer üzerinden eksik/boş/invalid/unsupported version, legacy command/outcome korunması, sağlıklı v1 continuation, kaldırılmış quarantine queue'ya zorunlu publish failure ve orijinalin redelivery/consumer stop davranışını doğrular. Aynı AMQP transfer helper'ıyla gerçek confirm sonrası source channel ACK öncesi kapatılır; ikinci teslimde iki aynı-ID/body kopyası ve değişmeyen Saga revision gözlenir. Bu son test broker ACK-window fault injection'ıdır; process crash/database recovery testi değildir. [`ParticipantQuarantineTests`](../tests/SagaFlow.Samples.Tests/ParticipantQuarantineTests.cs) gerçek sample worker'da legacy command'ı, quarantine kopyasının eski TTL'den uzun yaşamasını, boş business repository'yi, doğru v1 deferred sonucu ve transfer failure sonrası tüketimin durmasını sınar.

#### BPI-1A-03: eşzamanlı duplicate kanıtı

Varsayılan RabbitMQ consumer dispatch concurrency **1** olarak korunur; tek consumer'a paralel publish tek başına eşzamanlı state update kanıtı sayılmaz. [`ConcurrentBrokerOutcomesEnterTwoConsumersAndAtomicallyAdvanceOnlyOnce`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/RabbitMqReviewRemediationTests.cs), aynı gerçek queue üzerinde iki ayrı IHost/consumer channel ve aynı process-local store kullanır. İki bağımsız publisher channel aynı Saga/execution/command/MessageId/body'yi eşzamanlı gönderir. Store test wrapper'ı ilk iki `UpdateAsync` girişini bariyerde tutar; iki gerçek receiver da girmeden yarış serbest bırakılmaz. Gerçek atomik mutasyon mevcut InMemory store'da seri kritik bölge kullanır; wrapper bir mutation/idempotency mekanizması eklemez.

Assertion'lar: revision yalnız +1, bir completed step/result, yalnız bir next-step command, duplicate için gerçek ACK sonrası artan sayaç, boş source queue ve yanlış correlation/execution gönderiminden sonra değişmeyen revision/command sayısı. Mevcut [`LocalSagaCoordinationTests.ConcurrentDuplicateOutcomesAdvanceOnce`](../tests/SagaFlow.Engine.Tests/LocalSagaCoordinationTests.cs) 30 eşzamanlı Engine çağrısını ayrıca korur. Bu kanıt iki OS process/dağıtık durable store veya tek sequential consumer'ın iç paralelliği iddiası değildir. Production consumer concurrency veya Engine duplicate algoritması değiştirilmedi.

[`RabbitMqInvocationTransport`](../src/SagaFlow.Messaging.RabbitMQ/RabbitMqInvocationTransport.cs) bu participant protokolünden ayrıdır. BPI-1A aşamasında Source/Target/Invocation/Message metadata ve Payload bir teknik envelope'un ayrı alanlarıydı; BPI-1B artık bu metadata'yı header'larda, Payload'ı tek başına body'de taşır; business Payload içine teknik zorunlu alan eklenmedi. `InvokeAsync(target, payload)` değişmedi. Admission/acceptance/rejection/terminal fingerprint, hashed route, bounded parking ve eski invocation wire doğrulamaları korundu. Bu ayrı lifecycle'ı v1 participant header'larına taşımak, compatibility/parking replay kapsamını genişleteceğinden BPI-1A'da yapılmadı; bu önceki kapsam kararı BPI-1B invocation wire bölümüyle ilerletilmiştir. Procurement → Order success, manager rejection, compensation, source continuation ve invocation dedup mevcut gerçek broker regresyonlarıyla sınanır; S2 deneysel sonuçları bu protokol çalışmasından türetilmez.

Header'lar kalıcı state/checkpoint değildir. InMemory Saga ve deferred participant kayıtları restart'ta kaybolur; outbox/inbox, broker ACK ile state transaction'ı, publish belirsizliği, database-loss recovery ve attempt lifecycle bu görevde çözülmedi.

## BPI-1B: cross-domain invocation wire

Internal Core request/response modelleri Engine state sözleşmeleridir; RabbitMQ envelope'u değildir. `InvokeAsync(target, payload)` değişmedi. Adapter [`RabbitMqInvocationSerializer`](../src/SagaFlow.Messaging.RabbitMQ/RabbitMqInvocationSerializer.cs) request body'ye yalnız Payload JSON, Accepted/Rejected/Completed/Failed/Compensated body'ye JSON **null** yazar. Running için ayrı wire message mevcut değildir. Source external-step Result ayrımı artık aşağıdaki BPI-1C bölümüyle uygulanır; teknik result JSON'u üretilmez.

| Alan | AMQP mapping |
|---|---|
| Version / kind | `sf-version=1`, `sf-inv-kind` ve aynı `Type`: ProcessInvocationRequest/Accepted/Rejected/TerminalOutcome |
| Logical message / correlation | MessageId (UUID), CorrelationId (source-local correlation); InvocationId yerine kullanılmaz |
| Invocation | `sf-inv-id` |
| Processes | `sf-inv-source-domain/process/version`, `sf-inv-target-domain/process/version` |
| Source execution | `sf-inv-source-saga`, `sf-inv-source-step`, varsa `sf-inv-source-execution` (mevcut step.Execution.ExecutionId; yeni attempt modeli yok) |
| Time | `sf-inv-occurred-at`: orijinal UTC O timestamp; parser eksik zamanı üretmez |
| Reply | yalnız request ReplyTo = source process'in mevcut hashed outcome queue'su; doğrulanır |
| Lifecycle | response `sf-inv-status`; Accepted/terminal için gerçek `sf-inv-target-saga`; Rejected için `sf-inv-reason`, target Saga yok |
| Causation | varsa `sf-inv-causation-id`; request aktif command kimliğine, admission cevabı request MessageId'sine bağlıdır. Terminal için mevcut state son outcome MessageId'sini tutmadığından causation uydurulmaz. |

Target correlation mevcut mesaj modellerinde yoktur; source correlation ile birleştirilmez/üretilmez. Request'te henüz target Saga/status bulunamaz. Response body null olmalıdır. Zorunlu text/UTF-8 türleri, process/version, UUID'ler, timestamp, Type/kind, reply ve lifecycle status yeniden kurulurken doğrulanır. Engine mevcut stored identity/fingerprint/ownership kontrollerini korur. SourceExecutionId identity'nin opsiyonel provenance alanıdır; gerçek dispatch'te kayıtlı kimlik kullanılır, sentetik eski internal fixture'da yoksa algoritmayla türetilmez. MessageId/OccurredAt/state-generated retry kimlikleri korunur.

Participant `sf-ex-*` echo sözleşmesi ayrıdır: invocation iki SagaFlow Domain Starter arasında `sf-inv-*` taşır. Kuyruk/hash/exchange adları değişmez. Durable terminal parking artık business-null body **ve bütün properties/headers** taşır; bounded replay aynı serializer ile internal terminali yeniden kurar. Quarantine parking değildir: eksik/unsupported sf-version request/outcome ve eski parked envelope'lar BPI-1A'nın mandatory/confirm helper'ıyla ilgili source queue + `.quarantine` kuyruğuna aktarılır. Confirm sonrası source ACK; failure'da active invocation consumer callback dışından durdurulur. BPI-1D-01 (`Implemented`, bulgu `CLOSED`): parked legacy replay transferi veya cancellation başarısızsa kullanılabilir parking kanalında yalnız o delivery için tek `BasicNack(multiple:false, requeue:true, CancellationToken.None)` denenir; hata yeniden fırlatılır ve bounded çağrı sonlanır. Otomatik retry/hot requeue döngüsü yoktur. Topology düzeltildikten sonra aynı transport/kanal ile sonraki explicit replay ready mesajı tekrar görebilir. Quarantine confirm tamamlanmadan source ACK yoktur. NACK de başarısızsa iki hata AggregateException ile korunur ve critical log yazılır; kanal kapalıysa warning source requeue/ACK belirsizliğini bildirir. Kanalın kapanması/ACK belirsizliği başarılı requeue sayılmaz; broker ve olası quarantine kopyaları incelenmelidir. Bu legacy dalı terminal-processing catch blokları dışında olduğundan ikinci NACK uygulanmaz. Gerçek failure/cancellation/retry ve aynı-ID/body duplicate ile değişmeyen Saga state kanıtı: [`ParkedLegacyQuarantineTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ParkedLegacyQuarantineTests.cs). Confirm/ACK kopyaları mümkündür; otomatik legacy uygulama/dönüşüm yoktur.

Deployment öncesi eski in-flight/deferred invocation'ları eski sürümde tamamla/uzlaştır; yeni writer yalnız yeni format üretir, yeni reader legacy envelope'u tahmin etmez. Quarantine ham mesajı saklar, kaybolmuş InMemory source/target state'i kurtarmaz. Inbox/outbox, restart/database-loss recovery, yeni step retry veya merkezi coordinator eklenmedi. BPI-1C aşağıdaki bölümde `Implemented`; BPI-1D iki bulgu düzeltmesi `Implemented`, bağımsız kapanış kabulüne hazırdır. Kanıt: [`InvocationWireTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/InvocationWireTests.cs), mevcut [`ProcessInvocationIntegrationTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ProcessInvocationIntegrationTests.cs), [`ProcurementRabbitMqTests`](../tests/SagaFlow.Samples.Tests/ProcurementRabbitMqTests.cs); gerçek koşu sayıları [durum kaydında](01-PROJECT_STATUS.md).

## BPI-1C: business result ve coordination state

Önceki external continuation, terminal/rejection için `status`, `invocationId`, `targetSagaInstanceId`/`reason` içeren sentetik JSON'u SagaOutcomeEvent.Result üzerinden SagaStepState.Result'a yazıyordu. Bu yol kaldırıldı. [`SagaCoordinator`](../src/SagaFlow.Engine/Coordination/SagaCoordinator.cs) coordination sonucunu yalnız success/failure seçimi için kullanır; [`ProcessStateManager.RecordExternalCompletion`](../src/SagaFlow.Engine/ContextState/ProcessStateManager.cs) local external step Status/CompletedAt'ını günceller ve **Result=null** bırakır. Başka isimli teknik JSON result veya ikinci relationship store oluşturulmadı.

| Kavram | Sahip / temsil |
|---|---|
| Business result | participant sonucu `SagaStepState.Result`; compensation sonucu `SagaCompensationState.Result` |
| External business result yokluğu | nullable JsonElement HasValue=false; API JSON result=null. Participant'ın açık JSON null sonucu ise kendi business sonucu olarak korunabilir. |
| Invocation identity/source/target | step.Invocation.Identity ve existing IncomingInvocation |
| Remote lifecycle / observation | step.InvocationProgress.Status, TargetSagaInstanceId, LastObservedAt; rejection açıklaması opsiyonel RejectionReason |
| Local yürütüm | SagaInstance.Status, SagaStepState.Status/CompletedAt; remote status yerine geçmez |
| Manuel uzlaştırma | mevcut step.Reconciliation; sonuç geldi diye temizlenmez |

Completed → mevcut success, Failed/Compensated/Rejected → mevcut failure YAML kategorisi korunur. Transition hâlâ aynı atomik mutation içinde result/state recording sonrası seçilir; I/O dışarıdadır. Business-only Context/Input, diğer participant sonuçları ve compensation sonuçlarına dokunulmaz. External sonuç için business JSON üretilmediğinden `result.status` gibi eski teknik JSON koşulları MSG-01 ile definition validation hatasıdır; depodaki external YAML'lar success/failure transition kullanır, yeni coordination-query YAML özelliği eklenmedi. Normal business result'taki `status`/`invocationId` gibi alanlar isimlerine bakılarak silinmez veya teknik diye yeniden sınıflandırılmaz.

MSG-01 (`Implemented`, bulgu `CLOSED`): [`ProcessDefinitionValidator`](../src/SagaFlow.Engine/ProcessDefinition/ProcessDefinitionValidator.cs), mevcut [`ConditionParser`](../src/SagaFlow.Engine/ProcessDefinition/ConditionParser.cs) structured path doğrulaması ve legacy BooleanCondition.PropertyPath sonucundaki **path segmentlerini** kullanır. External step'in kendi `result.*` referansı ve herhangi bir step'ten `steps.<externalStep>.result[.*]` referansı reddedilir; fallback bulunması bu imkânsız business kaynağını geçerli yapmaz. Hata process/step/event/path ile “business Result is null” nedenini bildirir. Normal participant `result.status`, `steps.<participant>.result.*` ve gerçek `context.*` alanları geçerlidir; context altında result/steps adlarının geçmesi yasak oluşturmaz. Yeni coordination predicate dili eklenmedi.

Runtime yalnız load-time validation'a güvenmez. [`SagaCoordinator`](../src/SagaFlow.Engine/Coordination/SagaCoordinator.cs) aktif external completion'ın atomik continuation callback'inde mevcut validator ile tanımı yeniden doğrular. Kalıcı DefinitionValidationException veya TransitionResolver'ın InvalidSagaOutcomeException hatası local Saga/step Failed, CurrentStep=null, business Result=null, FailureReason ve mevcut InvocationReconciliation kaydı olarak commit edilir; sonraki step/compensation otomatik başlatılmaz. Receiver'ın önceden commit ettiği remote terminal status, target identity ve observed-message fingerprint korunur. Bu tanınan kalıcı hata transport'a retryable exception olarak dönmez; geçerli terminal normal ACK yolunu tamamlar. Source zaten Failed/TimedOut/inactive veya reconciliation açıksa mevcut guard continuation'ı atlar; duplicate ikinci transition üretmez. Store/cancellation/broker publish hataları bu catch'e alınmaz; mevcut geçici hata/redelivery yolu korunur. Source bir başka invocation'ın target'ıysa Failed terminalini yukarıya yayımlama hatası da halen retryable'dır; observation/yerel failure silinmez.

Kanıt: [`ExternalStepConditionTests`](../tests/SagaFlow.Engine.Tests/ExternalStepConditionTests.cs) direct/indirect/legacy/YAML hata mesajını ve geçerli business kaynaklarını; [`ExternalConditionRuntimeTests`](../tests/SagaFlow.Engine.Tests/ExternalConditionRuntimeTests.cs) bypassed definition, no-match context, Failed/reconciliation ve duplicate güvenliğini; [`ExternalConditionRedeliveryTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ExternalConditionRedeliveryTests.cs) gerçek broker NACK/redelivery → aynı adapter ACK, retained observation, sıfır continuation ve sabit delivery sayısını sınar. Testte yalnız eski/bypass edilmiş tanımı ve delivery gözlemini oluşturmak için reflection kullanılır; üretime bypass API eklenmedi. [Gerçek koşu kaydı](01-PROJECT_STATUS.md).

Source Failed/TimedOut, inactive/completed step ve açık reconciliation otomatik continuation'ı engeller. Duplicate suppression Result.HasValue'ya dayanmaz; active step/status ve mevcut invocation observation/fingerprint guard'ları kullanılır. İlk terminal observation ve local continuation iki ayrı mevcut commit'tir; crash penceresi/durable recovery bu aşamada çözülmedi.

Dashboard detail `steps[].result` business alanıdır; `steps[].invocation` ve relationship API mevcut Invocation/InvocationProgress/IncomingInvocation/Reconciliation'dan teknik metadata üretir. Source LastKnownStatus ile TargetLocalStatus birleştirilmez. RejectionReason bu teknik read modellerine eklendi; result içinden okunmaz. BPI-1D-02 (`Implemented`, bulgu `CLOSED`): gerçek JSON alanı `rejectionReason`, frontend tipi opsiyonel `string | null` olur. Source Instance Traceability ve Source BigPicture Runtime Relationship Drawer yalnız gerekçe varsa teknik “Rejection reason” alanını gösterir; null/eksik alanda boş satır oluşmaz. LastKnownStatus, TargetLocalStatus, reconciliation ve bağımsız kimlikler korunur. Mevcut semantic dl/dt/dd, dark/light renkleri ve uzun metin wrapping kullanılır; yeni fetch/hook/state yoktur. [`rejectionReason.test.tsx`](../src/SagaFlow.Dashboard.Client/tests/rejectionReason.test.tsx) iki panelde reason/null ve business-result decoy vakalarını sınar; [`ProcurementRabbitMqTests`](../tests/SagaFlow.Samples.Tests/ProcurementRabbitMqTests.cs) gerçek HTTP detail/relationship alanlarını external Result=null ile doğrular. UI yeniden tasarlanmadı. Kaynak: [`SagaFlowDashboardEndpoints`](../src/SagaFlow.Hosting/Endpoints/SagaFlowDashboardEndpoints.cs), [`InstanceRelationshipView`](../src/SagaFlow.Hosting/Dashboard/InstanceRelationshipView.cs).

Kanıt: [`CrossDomainCoordinationTests`](../tests/SagaFlow.Engine.Tests/CrossDomainCoordinationTests.cs) terminal üçlüsü, rejected reason, duplicate, Failed/TimedOut/reconciliation guard ve farklı participant/compensation business şemalarını doğrular. [`ProcessInvocationIntegrationTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/ProcessInvocationIntegrationTests.cs) broker source completion'da Result=null + doğru InvocationProgress + CompletedAt kontrol eder. [`ProcurementRabbitMqTests`](../tests/SagaFlow.Samples.Tests/ProcurementRabbitMqTests.cs) success/business rejection/compensation/failed senaryolarında external Result=null ve doğru step/remote status'u doğrular; mevcut gerçek Dashboard/relationship API kontrolleri korunur.

Cross-domain terminal business result transport'u yoktur; BPI-1B lifecycle null-body wire, BPI-1A participant wire ve deferred/context isolation korunur. BPI-1D iki MEDIUM bulgusu giderildi; güncel regresyon ve bağımsız kapanış kabulü kapsamı [durum kaydındadır](01-PROJECT_STATUS.md); yeni recovery/typed model-binding/SDK özelliği tamamlanmış sayılmaz. [Güncel koşu kaydı](01-PROJECT_STATUS.md).

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

Uygulama gerçek bir `ISagaMessageBus` adapter'ını DI'a kaydetmelidir. `SagaFlow.Messaging.RabbitMQ` adapter'ı `AddSagaFlowRabbitMq` ile ayrıca kaydedilebilir. Adapter yokken host YAML yükleyebilir ve tanım keşfi/dashboard hosting çalışabilir; `StartAsync` ise instance yaratmadan açık eksik-transport hatası verir. Testlerdeki kayıt yapan fake'ler yalnızca test projelerindedir.

## Instance ve ileri step modeli

Mevcut `SagaInstance` genişletilmiştir:

- `Id`: mevcut string API'si korunarak Guid'nin `N` biçiminde saklanır; `StartAsync` Guid döndürür. `ISagaStateStore.FindAsync(Guid)` kolaylığı da vardır.
- `DefinitionName`, `DefinitionDomain`, `DefinitionVersion`: tanımın tam kimliği; süreç ilerlerken yalnızca bu yerel kayıt kullanılır.
- `CorrelationId`, `Status`, `CurrentStep`, `CreatedAt`, `UpdatedAt`, `FailureReason`.
- `Context`: başlangıç payload'ının bağımsız `JsonElement` karşılığı; System.Text.Json web/camelCase ayarları kullanılır. Null, primitive, array ve object JSON desteklenir; döngülü veya desteklenmeyen payload reddedilir.
- `Steps`, `Compensations`, `Revision`: dinamik state listeleri ve store concurrency token'ı.

Her YAML executable step'i için tek `SagaStepState` oluşturulur. `StepName` korunmuştur; `StepId` aynı YAML adını döndürür. State alanları immutable `Execution` context'i, `Command`, `ExecutionOrder`, `Input`, `Result`, `StartedAt`, `CompletedAt`, `TimeoutLimit`, `TimedOutAt` içerir. Başlangıçta hepsi `Pending` ve sırasızdır. Ayrı `SagaStepStatus` enum'u `Pending`, `Dispatched`, `Completed`, `Failed`, `TimedOut` değerlerini içerir; eski scaffold'ın genel `SagaStatus` alanı yerine ileri yürütüme özgü durumlar kullanılır.

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
    // Adapter wire properties'den okur; participant yalnız orijinal context'i echo eder.
    Headers = command.Headers,
    CausationId = command.Headers.CommandId,
    IsSuccess = true,
    Result = JsonSerializer.SerializeToElement(new { available = true })
});
```

BPI-1A'daki tam kayıtlı execution context ve instance/correlation/step eşleşmesi, aktif Saga ve step'in `Dispatched` olması atomik güncelleme içinde doğrulanır. Tamamlanmış/başarısız step outcome'u tekrar kabul edilmez. Aynı outcome eşzamanlı gelse de yalnızca biri yeni transition ve command oluşturur. Bu, instance bellekte kaldığı sürece yerel duplicate korumasıdır; katılımcı business side effect idempotency'si veya durable inbox değildir.

`IsSuccess`, YAML'daki `success`/`failure` kategorisine eşlenir; mevcut `Name` alanı routing/subscription mekanizması değildir. `TransitionResolver`, mevcut `ConditionParser` AST'sini kullanır. Parser yalnızca sözdizimini ayrıştırır; `ProcessDefinitionValidator` YAML yüklenirken yalnızca `result.*` köklü, case-sensitive Boolean property yollarını kabul eder. `data.*`, `context.*` ve köksüz koşullar process, step ve condition bilgisiyle reddedilir. Boolean `==`/`!=` ve iç içe property yolları desteklenir. Çağrı/reflection/scripting yoktur. Runtime'da eksik property, yanlış JSON tipi veya savunma amaçlı kök kontrolü açık hata üretir; sessizce false veya else kabul edilmez.

Eşleşen kategorinin koşulları önce değerlendirilir; eşleşme yoksa aynı kategorideki `else: true` veya unconditional default kullanılır. YAML liste sırası kararı değiştirmez. Yanlış metadata veya değerlendirilemeyen sonuç state'i değiştirmeden reddedilir; doğru sonuç daha sonra işlenebilir. Kategoride hiçbir kural yoksa diğer kategorinin fallback'i kullanılmaz.

Kabul edilen outcome'un forward state'i ve bir sonraki command claim'i tek atomik güncellemede saklanır. `completed` Saga'yı `Completed`, `compensation` ise `CompensationRequested` yapar; terminal durumlarda CurrentStep temizlenir. Validator, listedeki ilk step üzerinden erişilebilen executable graph üzerinde DFS aktif yol kontrolüyle self-loop ve çok adımlı döngüleri, terminal çıkışları olsa bile reddeder; hata döngünün step adlarını içerir. Tamamlanmış DFS düğümlerine birleşen dallar geçerli DAG yapılarıdır. Önceki erişilebilirlik/terminal yol kontrolleri ve exhaustive koşullardan sonraki erişilemez fallback'in dışlanması korunur. Runtime'ın tek-seferlik dispatch savunması da korunur: daha önce çalıştırılmış bir step'e dönüş Saga'yı `Failed` yapar ve açık cycle/revisit hatası üretir.

## Compensation planı

YAML `SagaCompensationDefinition` tanımsal modeldir. Ayrı `SagaCompensationState`, yalnız compensation tanımlı step'ler için oluşturulur; `Execution`, `StepId`, `Command`, `Status`, `ExecutionOrder`, `StartedAt`, `CompletedAt`, `Result` alanlarını taşır. `Pending`, `Planned`, `Dispatched`, `Completed`, `Failed` durumları vardır. [`CompensationManager`](../src/SagaFlow.Engine/Consistency/CompensationManager.cs) planı birer command ile yürütür ve bağımsız business result'ları kaydeder.

Compensation istendiğinde yalnız `Completed` forward step'lerin compensation kayıtları ters forward `ExecutionOrder` sırasıyla planlanır; başarısız/çalışmamış veya compensation tanımsız adımlar alınmaz. Forward geçmiş korunur. Her Planned → Dispatched claim'i yeni execution/command kimliği, `compensation` kind ve ilgili forward ExecutionId ile business-only command hazırlar. Başarılı compensation outcome'u sonraki compensation'ı başlatır; hata planı durdurur. Sonraki domain rollback mevcut mekanizmayı kullanır. BPI-1A orkestrasyon sırasını değiştirmez; wire/kimlik ayrımını ve atomik outcome doğrulamasını ekler. Kanıt: [`CompensationExecutionTests`](../tests/SagaFlow.Engine.Tests/CompensationExecutionTests.cs), [`RabbitMqWireContractTests`](../tests/SagaFlow.Messaging.RabbitMQ.Tests/RabbitMqWireContractTests.cs).

## InMemory Store ve concurrency

Güncel Return incelemesi (REV-02, 2026-11-03): outcome uygulandıktan sonra sıradaki command publish hatası [`SagaStepExecutor`](../src/SagaFlow.Engine/Coordination/SagaStepExecutor.cs) tarafından `Failed` durumuna yazılır. [`RabbitMqTransport`](../src/SagaFlow.Messaging.RabbitMQ/RabbitMqTransport.cs) bu `SagaDispatchException` sonrasında önceki outcome'u ACK eder. Duplicate teslim, uygulanmış outcome'u yeniden yürütmeye yetmez; ACK yerine basit NACK recovery değildir. Publish sonucunun belirsiz kaldığı pencere ve InMemory host restart kaybı için otomatik uzlaştırma yoktur. Bu güncel sınır, aşağıdaki tarihsel ilk PoC kayıtlarını değiştirmez; [Return inceleme ayrıntısı](07-return-workflow.md) ve [Engine publish hata testi](../tests/SagaFlow.Engine.Tests/LocalSagaCoordinationTests.cs) kapsamı gösterir.

PA-01 test izolasyonu: [`scripts/run-rabbitmq-integration.ps1`](../scripts/run-rabbitmq-integration.ps1) mevcut `samples/docker-compose.yml` brokerının sağlıklı olmasını bekler, her koşu için benzersiz bir test vhost'u oluşturur, `sagaflow` test kullanıcısına bu vhost üzerinde izin verir ve iki entegrasyon bayrağıyla izole .NET build/test çalıştırır. `SAGAFLOW_RABBITMQ_USERNAME` ve `SAGAFLOW_RABBITMQ_PASSWORD` ortamda verilmelidir; script parola saklamaz. Windows PowerShell örneği: `$env:SAGAFLOW_RABBITMQ_USERNAME='sagaflow'; $env:SAGAFLOW_RABBITMQ_PASSWORD='<Compose parolası>'; ./scripts/run-rabbitmq-integration.ps1`. Script, örneklerin `/` vhost'unu değiştirmeden test process'lerine `SAGAFLOW_RABBITMQ_TEST_VHOST` ve `RabbitMq__VirtualHost` aktarır; sonunda yalnız oluşturduğu vhost'u siler. Transport fixture'ı ve brokerlı örnek testler bu iki ayar olmadan `/` üzerinde çalışmayı reddeder. Domain tabanlı `order.outcomes` ve ReplyTo adları değişmez; vhost sınırı kaynak çakışmasını önler. Test vhost'u silinemeyip uyarı verilirse adı logdan alınarak elle temizlenmelidir. [Transport fixture](../tests/SagaFlow.Messaging.RabbitMQ.Tests/RabbitMqBrokerFixture.cs) ve [örnek broker testleri](../tests/SagaFlow.Samples.Tests/SampleRabbitMqEndToEndTests.cs) yapılandırmayı doğrular.

PBI-01 ile [`InMemorySagaStateStore`](../src/SagaFlow.Storage.InMemory/Services/InMemorySagaStateStore.cs), yalnızca Core'a proje referansı olan [SagaFlow.Storage.InMemory](../src/SagaFlow.Storage.InMemory/SagaFlow.Storage.InMemory.csproj) kütüphanesine taşındı. [Core](../src/SagaFlow.Core/Abstractions/ISagaStateStore.cs) store sözleşmesini ve state modellerini içerir. [Engine](../src/SagaFlow.Engine/SagaFlow.Engine.csproj) koordinasyonda bu sözleşmeyi kullanır. Store ve Engine, Core'daki internal [snapshot yardımcısını](../src/SagaFlow.Core/State/SagaStateSnapshot.cs) paylaşır. [SagaStateEntry](../src/SagaFlow.Storage.InMemory/Models/SagaStateEntry.cs) kayıt başına snapshot ve kilit bilgisini tutar. [IncomingInvocationStore](../src/SagaFlow.Storage.InMemory/Services/IncomingInvocationStore.cs) aynı instance indeksini kullanarak claim ve admission kararlarını yönetir. State process içindeki dictionary'de saklanır; her instance ayrı kilit kullanır. `SaveAsync` yeni kaydı revision 0 ile kabul eder; var olan kaydı yalnızca güncel revision eşleşiyorsa değiştirir. Stale read/modify/write sessiz veri kaybı yerine hata üretir.

Yeni `UpdateAsync<TResult>` senkron ve side-effect-free callback ile atomik değişiklik yapar. Callback bağımsız çalışma kopyasını alır; exception veya iptal varsa değişiklik commit edilmez. Callback içinde I/O, await veya nested state write yapılmamalıdır; aynı instance'a nested write açıkça reddedilir. Mesaj publish bu callback'in dışındadır, dolayısıyla transport senkron olarak outcome döndürse de store kilidinde deadlock oluşmaz.

Save, Find ve Update commit sınırlarında listeler, step/compensation nesneleri ve JSON değerleri deep-copy edilir. Dışarı dönen veya callback'ten tutulan referanslar kayıtlı state'i değiştiremez. `Revision` her başarılı yazmada artar. Coordinator yalnızca portu kullanır; gelecekte database adapter'ı aynı atomic-update/optimistic concurrency semantiğini sağlamalıdır. Şu anda database, migration, distributed lock veya restart recovery yoktur.

## DI kayıtları

| Servis | Lifetime |
|---|---|
| `ISagaStateStore` → `SagaFlow.Storage.InMemory.InMemorySagaStateStore` | Singleton. `AddSagaFlow()` varsayılan olarak `TryAddSingleton` ile kaydeder. |
| `ISagaClock` → `SystemSagaClock` | Singleton; değiştirilebilir |
| `ProcessDefinitionRegistry` | Singleton; önceki davranış |
| `TransitionResolver`, `ProcessStateManager` | Singleton; stateless |
| `SagaInstanceManager`, `SagaStepExecutor`, `CommandDispatcher` | Scoped |
| `SagaCoordinator`, aynı instance'a eşlenen `ISagaFlow` | Scoped |
| `OutcomeEventReceiver` | Scoped |
| `ISagaMessageBus` | Varsayılan kayıt yok; uygulamanın adapter'ı |

Scoped transport desteklenir; singleton coordinator içinde scoped transport tutulmaz. [Hosting](../src/SagaFlow.Hosting/SagaFlow.Hosting.csproj) varsayılan store'u doğrudan referans alır. Store adapter'ı `AddSagaFlow()` öncesinde veya sonrasında değiştirilebilir. Somut store tipini doğrudan kullanan kod yeni assembly referansına ve namespace'e geçmelidir.

## Testler ve sınırlar

```powershell
dotnet build .\SagaFlow.slnx --no-restore
dotnet test .\SagaFlow.slnx --no-build --no-restore
```

`LocalSagaCoordinationTests`, `RuntimeTransitionTests`, `InMemorySagaStateStoreTests` ve `LocalSagaCoordinationHostingTests` gerçek runtime sınıfları ve gerçek InMemory state store üzerinde çalışır; command publish yalnızca test fake'leri üzerinden doğrulanır. Hosting testleri gerçek YAML startup ve DI scope doğrulaması kullanır; gRPC/cross-process tanım keşfi testleri de korunmuştur.

PBI-01 (2026-11-09) odak koşusunda 58 test geçti (Engine 26, Hosting 23, RabbitMQ 1, Samples 8). Başarısız veya atlanan test yok. Store testleri snapshot izolasyonunu, eşzamanlı atomik güncellemeleri, revision kontrolünü ve rollback'i kapsar. [IncomingClaimTests](../tests/SagaFlow.Engine.Tests/IncomingClaimTests.cs) ve [ProcessMessagingTests](../tests/SagaFlow.Engine.Tests/ProcessMessagingTests.cs) claim, admission ve duplicate davranışını doğrular. [Hosting testleri](../tests/SagaFlow.Hosting.Tests/LocalSagaCoordinationHostingTests.cs) varsayılan singleton ve adapter değişimini sınar. Gerçek RabbitMQ ile [Order](../tests/SagaFlow.Samples.Tests/SampleRabbitMqEndToEndTests.cs), [Return](../tests/SagaFlow.Samples.Tests/ReturnRabbitMqEndToEndTests.cs) ve [Procurement](../tests/SagaFlow.Samples.Tests/ProcurementRabbitMqTests.cs) senaryoları geçti. [İzole RabbitMQ runner](../scripts/run-rabbitmq-integration.ps1) tam koşuda 662 test geçirdi (Core 2, Engine 320, Hosting 64, RabbitMQ 62, Samples 214). Başarısız veya atlanan test yok. İki solution derlemesinde 0 uyarı ve 0 hata görüldü. Runner her koşunun vhost'unu sildi. İsteğe bağlı Chrome kontrolleri çalıştırılmadı. Loglar `.artifacts/pbi01-focused.log` ve `.artifacts/pbi01-full.log`.

Model ve servis ayrımı sonrası `./scripts/run-rabbitmq-integration.ps1` tekrar çalıştırıldı. 662 test geçti (Core 2, Engine 320, Hosting 64, RabbitMQ 62, Samples 214). Derlemede 0 uyarı ve 0 hata, testlerde 0 başarısız ve 0 atlanan vaka kaydedildi. Test vhost'u temizlendi. İsteğe bağlı Chrome kontrolleri çalıştırılmadı. Log `.artifacts/pbi01-organization-full.log`.

İlk runtime geliştirmesinin referansı: solution build sıfır uyarı ve sıfır hatayla geçti. Toplam 157 test geçti (Core 1, Engine 121, Hosting 35); başarısız veya atlanan test yok. Önceki 98 test korundu; 59 yeni davranış vakası eklendi. Testler 2 ve 10 adımlı akışları, conditional/else dallarını, ters compensation planını, 500 eşzamanlı atomik state güncellemesini, 30 eşzamanlı duplicate outcome'u, scoped DI, transport hataları ve cancellation sınırlarını kapsar.

LC-01/LR-01/LC-02 review düzeltmelerinden sonra: odaklı Engine testleri 123/123 geçti. Yukarıdaki tam solution build komutu sıfır uyarı/hata ile, tam test komutu 178 başarılı testle geçti (Core 1, Engine 142, Hosting 35); başarısız/atlanan yok. Net 21 regresyon vakası eklendi: YAML condition kökleri, YAML'dan runtime Boolean değerlendirmesi, terminal çıkışlı döngülerin reddi ve DAG birleşmeleri. Eski parser örneği `result.*` ile hizalandı; terminal çıkışlı döngüyü kabul eden test ret beklentisine çevrildi. Runtime revisit testi artık geçerli bir DAG üzerinde tutarsız state geçmişi enjekte ederek mevcut dispatch savunmasını doğrular; hiçbir test kaldırılmadı veya devre dışı bırakılmadı. Publish failure davranışı mevcut ilk/sonraki command hata testleriyle doğrulanır; production runtime kodu değiştirilmedi.

## İlk runtime geliştirmesinin dosya kapsamı

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

[`ISagaStateStore`](../src/SagaFlow.Core/Abstractions/ISagaStateStore.cs) `GetOrCreateIncomingAsync` ve `AttachIncomingAsync` atomik claim sözleşmeleri sunar. Desteklemeyen adapter varsayılan olarak NotSupportedException üretir; lookup-then-create fallback yapılmaz. [`InMemorySagaStateStore`](../src/SagaFlow.Storage.InMemory/Services/InMemorySagaStateStore.cs) store'a özel InvocationId → TargetSagaId indeksi ile claim işlemlerini seri yürütür. İlk request'in tam identity/payload'ı incoming kaydında tutulur. Aynı invocation tekrarında instance creation factory çağrılmaz; mevcut target snapshot döner. Aynı ID farklı source/target/correlation/payload ile gelirse conflict'tir. Başka var olan target'a aynı invocation bağlanamaz. Normal SaveAsync/UpdateAsync incoming provenance'ı değiştiremez veya silemez; indeks bu yollardan atlanamaz. Aynı içeriğin serialize/deserialize edilmiş kopyası normal revision kontrollü Save ile korunabilir.

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

[`RabbitMqInvocationTransport`](../src/SagaFlow.Messaging.RabbitMQ/RabbitMqInvocationTransport.cs) ayrı durable direct exchange `sagaflow.process-invocations` kullanır. Queue/routing key: `sagaflow.invocations.{SHA256(System.Text.Json ile ProcessIdentity UTF-8 serialization)}.requests` veya `.outcomes` (uppercase hex). Hash tam domain/process/version içerir; kimlik BPI-1B sf-inv header'larında açık taşınır ve alıcıda doğrulanır. Her host tek Local tanım/sürüm için iki queue tüketir. Participant exchange/step queue'ları ve `{domain}.outcomes` yolu değiştirilmedi. gRPC endpoint hiçbir zaman mesaj adresine çevrilmez. Request ReplyTo, source identity'den türetilmiş outcome queue'dur; alıcı bunu doğrular, keyfi reply adresini takip etmez. Type/kind eşleşmesi ve MessageId/CorrelationId zorunluluğu BPI-1B parser'ında doğrulanır; Engine stored identity kontrolü korunur.

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

Completed → success; Failed/Compensated → failure; business Rejected → failure event'i mevcut TransitionResolver/ProcessStateManager'a verilir. YAML uygun success/failure politikalarını tanımlamalıdır. S2-4'te teknik result JSON'u üretiliyordu; BPI-1C sonrası external business Result=null, teknik status/identity/reason Invocation/InvocationProgress'tedir; uzak business result payload'ı icat edilmez. Structured condition'lar InvocationProgress teknik alanlarını veya external business Result'unu okuyamaz; mevcut business context/normal participant sonuçlarını okuyabilir. MSG-01 validation ve runtime sınırları BPI-1C bölümünde açıklanır. Failure transition compensation veya recovery step seçebilir; source'un son Saga status'u kendi YAML/rollback politikasının sonucudur, remote failure otomatik source Completed anlamına gelmez.

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
