# Return Domain ve Warehouse participant

## Sahiplik ve ilişki

[`SagaFlow.Return`](../samples/ReturnProcess/SagaFlow.Return/Program.cs) kendi `return-process.yaml` tanımını, `return.outcomes` kuyruğunu, InMemory Saga state'ini ve gömülü dashboard'unu barındırır. [`SagaFlow.Order`](../samples/OrderProcess/SagaFlow.Order/Program.cs) ayrı kalır. [`CreateReturnRequest`](../samples/ReturnProcess/SagaFlow.Return/ReturnModels.cs) bir `OrderId` kabul eder; Return kendi `ReturnId` ve Saga instance ID'sini üretir. Order kaydını sorgulama veya sahipliğini doğrulama bu PoC'de yoktur. `OrderId` iş ilişkisi, Saga instance ID ve correlation ID ise ayrı yürütüm kimlikleridir. [`ReturnsController`](../samples/ReturnProcess/SagaFlow.Return/Controllers/ReturnsController.cs) `GET /returns/{returnId}` ile bu kimlikleri ve türetilen iş durumunu verir.

Return iş API uçları controller içinde tanımlıdır; controller doğrulama ve HTTP yanıtını yönetir, DI ile kayıt oluşturma/Saga başlatma ve iş durumu sorgusunu [`ReturnService`](../samples/ReturnProcess/SagaFlow.Return/ReturnService.cs) üzerinden çağırır. `Program.cs` servis kaydı ve controller eşlemesini yapar. `MapSagaFlowDefinitionDiscovery` genel Hosting keşif endpoint'idir, Return iş API'si değildir. Normal Return geliştirme launch profili bu endpoint'i `return/return-process@1.0` tanımı için açar; profil dışındaki varsayılan yayın kapalıdır.

## YAML akışları

Kaynak: [`return-process.yaml`](../samples/ReturnProcess/SagaFlow.Return/saga/return-process.yaml).

| Warehouse kararı / teknik sonuç | YAML yolu | İş sonucu |
|---|---|---|
| `accepted: true` | `warehouse → payment → inventory → acceptedNotification → completed` | `Accepted`; Saga `Completed` |
| `accepted: false` | `warehouse → rejectedNotification → completed` | `Rejected`; Saga `Completed` |
| Teknik failure | İlgili adım → `compensation` | Refund başlamamışsa `Failed`; refund tamamlandıysa `ManualReview` |

Ret bir business kararıdır: [`WarehouseDecisionService`](../samples/Participants/SagaFlow.Warehouse/WarehouseDecisionService.cs) başarılı outcome içinde `accepted: false` gönderir, YAML dalı seçer. Genel Saga modeline `Rejected` terminal durumu eklenmez. `ManualReview` uygulama iş durumudur; otomatik kurtarma iddiası değildir.

## Participant sözleşmeleri

- [`WarehouseCommandHandler`](../samples/Participants/SagaFlow.Warehouse/WarehouseCommandHandler.cs) `InspectReturn` komutunu RabbitMQ üzerinden alır, Saga/step/correlation/ReplyTo ile [`WarehouseRepository`](../samples/Participants/SagaFlow.Warehouse/WarehouseRepository.cs) içinde `Pending` kayıt oluşturur ve outcome'u erteler. `GET /inspections/pending`, `GET /inspections/{returnId}` ve `POST /inspections/{returnId}/decision` API'leri [controller](../samples/Participants/SagaFlow.Warehouse/Controllers/InspectionsController.cs) içindedir. Swagger development ortamında açıktır. `accept` ve `reject` kararları publisher confirm ile orijinal ReplyTo adresine gönderilir. Eşzamanlı kararlardan yalnız biri claim edilir; aynı komut kaydı sıfırlamaz. Yayının kesin başlamadığı durumda aynı karar tekrar denenebilir; yayın sonucunun belirsiz kaldığı durumda otomatik yeniden gönderim engellenir.
- Warehouse controller, listeleme/sorgulama ve karar işlemini DI ile [`WarehouseInspectionService`](../samples/Participants/SagaFlow.Warehouse/WarehouseInspectionService.cs) üzerinden çağırır. Bu servis mevcut `WarehouseRepository` ve `WarehouseDecisionService` davranışını kullanır; HTTP sözleşmesi ve karar yayım kuralları değişmemiştir.
- [`PaymentCommandHandler`](../samples/Participants/SagaFlow.Payment/Messaging/PaymentCommandHandler.cs) `RefundReturnPayment` adlı **normal ileri adımı** işler. [`PaymentService`](../samples/Participants/SagaFlow.Payment/Services/PaymentService.cs) `ReturnId` üzerinden idempotent iade kaydı tutar; mevcut `RefundPayment` Order compensation komutundan ayrıdır. Gerçek ödeme sağlayıcısı yoktur.
- [`InventoryCommandHandler`](../samples/Participants/SagaFlow.Inventory/Messaging/InventoryCommandHandler.cs) `RestockReturn` işler. [`InventoryService`](../samples/Participants/SagaFlow.Inventory/Services/InventoryService.cs) aynı `ReturnId`/OrderId/ürün listesi için tekrar komutu tek kayda indirger; farklı içerik hata verir.
- [`NotificationCommandHandler`](../samples/Participants/SagaFlow.Notification/Messaging/NotificationCommandHandler.cs) `NotifyReturnAccepted` ve `NotifyReturnRejected` komutlarını işler. [`NotificationService`](../samples/Participants/SagaFlow.Notification/Services/NotificationService.cs) sonucu InMemory kaydeder; gerçek e-posta/SMS gönderimi yoktur.

Paylaşılan [`RabbitMqParticipantWorker`](../samples/Participants/Shared/RabbitMqParticipantWorker.cs) komutları YAML step adına karşılık gelen kuyruğa bağlar ve outcome'u Return Domain'e yollar. Katılımcılar bir sonraki Saga adımını seçmez.

## Hata ve dayanıklılık sınırı

Warehouse teknik hatası, refund hatası, restock hatası ve her iki notification hatası için gerçek broker testleri vardır. Refund başarıyla gerçekleşip sonraki adım başarısız olduğunda finansal işlem geri alınmış gibi gösterilmez: Return iş durumu `ManualReview` olur. Bu PoC'de iade/refund için reverse-refund compensation yoktur. `ReturnId` temelli idempotency yalnız participant process belleği yaşarken geçerlidir. Warehouse `Pending` kararı da InMemory'dir; karar öncesi Warehouse restart, kabul edilmiş komutu geri getirmez. Broker ACK ile process state arasında kalıcı transaction, durable inbox/outbox, exactly-once veya otomatik restart recovery yoktur. Return host restart olursa Saga ve Return kaydı kaybolur. Worker restart sonrası **yeni** Return komutlarının işlenmesi test edildi; bekleyen kararın restart recovery'si test edilmedi ve desteklenmez.

`/sagaflow` Return hostunda aynı genel dashboard'u sunar. `/sagaflow/api/definitions` warehouse, accepted ve rejected dallarını; instance API yerel adım durumlarını döndürür. Order ve Return için merkezi dashboard veya birleşik runtime trace yoktur. gRPC Big-Picture tanım keşfi statiktir; runtime ilişki/iz değildir.

## Doğrulama

[`ReturnDomainTests`](../tests/SagaFlow.Samples.Tests/ReturnDomainTests.cs) YAML, refund/restock/bildirim idempotency'si, deferred outcome, duplicate/conflicting command ve eşzamanlı karar claim'ini kapsar. [`ReturnRabbitMqEndToEndTests`](../tests/SagaFlow.Samples.Tests/ReturnRabbitMqEndToEndTests.cs) RabbitMQ ile accepted/rejected, teknik hatalar, yanlış correlation, worker restart, Order/Return ortak participant kullanımı ve Return dashboard HTTP/API'sini kapsar. Brokerlı testler PA-01 sonrası her koşu için ayrı vhost üzerinde çalışır; kurulum [mesajlaşma/test yönergesinde](04-local-saga-coordination.md), son tam sonuç [01-PROJECT_STATUS.md](01-PROJECT_STATUS.md) içindedir.

## Bağımsız inceleme düzeltmeleri ve açık sınırlar (2026-11-03)

- **REV-01 kapatıldı.** YAML başarı dalları artık `steps.warehouse.result.accepted` için açık `true` ve `false` koşulları kullanır. Eksik, `null` veya Boolean olmayan değer eşleşmez. [`TransitionResolver`](../src/SagaFlow.Engine/Coordination/TransitionResolver.cs) `InvalidSagaOutcomeException` üretir; atomik store güncellemesi geri alınır ve RabbitMQ geçersiz outcome'u requeue etmeden reddeder. Saga `Running`, Warehouse adımı `Dispatched` kalır; geçerli outcome veya operatör incelemesi gerekir. [Engine testi](../tests/SagaFlow.Engine.Tests/StructuredConditionalTransitionTests.cs) tüm değerleri, [Return broker testi](../tests/SagaFlow.Samples.Tests/ReturnRabbitMqEndToEndTests.cs) gerçek YAML ile bozuk değerlerden sonra geçerli kararın yürütülmesini sınar.
- **REV-03 durumları doğrulandı.** Yayım başlamadan hata `PublishNotSent` olur; yalnız aynı karar tekrar denenebilir. Yayım başladıktan sonra belirsiz hata `PublishFailed` olur; aynı veya karşıt kararın tekrar yayımı engellenir. `GET /inspections/{returnId}` kararı ve hata durumunu gösterir. Broker ile Return Saga durumunun elle uzlaştırılması gerekir. [Warehouse testleri](../tests/SagaFlow.Samples.Tests/ReturnDomainTests.cs) her iki hata ve tekrar kararını sahte yayıncıyla doğrular. InMemory kayıt restart sonrası kaybolur.
- **REV-02 kabul edilen PoC sınırı.** Outcome yerel state'e uygulanıp sonraki komut hazırlandıktan sonra publish başarısız olursa [`SagaStepExecutor`](../src/SagaFlow.Engine/Coordination/SagaStepExecutor.cs) adımı ve Saga'yı `Failed` yapar. [`RabbitMqTransport`](../src/SagaFlow.Messaging.RabbitMQ/RabbitMqTransport.cs) `SagaDispatchException` için önceki outcome'u ACK eder. NACK ile yeniden teslim, önceden uygulanmış outcome'u duplicate sayacağından güvenilir replay sağlamaz. Belirsiz publish sonucunda komut brokerda bulunabilir; etkiler elle uzlaştırılmalıdır. Host restart InMemory kanıtı siler. [Engine testi](../tests/SagaFlow.Engine.Tests/LocalSagaCoordinationTests.cs) görünür `Failed` durumunu doğrular; crash recovery kanıtı değildir.
