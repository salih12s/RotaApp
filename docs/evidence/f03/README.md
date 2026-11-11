# F03 kanıtları (2026-11-11)

| Dosya | İçerik |
|---|---|
| `build.txt` | `dotnet build RotaApp.slnx -warnaserror` → 0 uyarı, 0 hata |
| `all-tests.txt` | `dotnet test RotaApp.slnx` → 306/306 (F02'den 290; +8 sözleşme, +8 mesajlaşma) |
| `messaging-tests.txt` | `dotnet test tests/Rota.Orders.IntegrationTests --filter-trait "Category=Messaging"` → 8/8, test test, süreleriyle |
| `static-checks.txt` | FAIL sinyali taramaları: `BasicPublishAsync`, `autoAck`, ACK/commit sırası, saat, kültür, yasak paket |

## PASS kriteri → test

| # | Kriter | Durum | Test |
|---|---|---|---|
| 1 | R-6: aynı olay iki kez → tek yan etki | ✅ | `Consume_SameEventDeliveredTwice_HasExactlyOneSideEffect`: 1 `dispatch_jobs`, 1 `DispatchStarted` outbox satırı, 1 inbox satırı, DLQ boş |
| 2 | R-7: broker kapalıyken 50 sipariş → açılınca 50 `Dispatching`, kayıp 0 | ✅ | `Outage_FiftyOrdersCreatedWhileBrokerIsDown_…`: kesintide 50 `OrderCreated` outbox'ta bekliyor (sayı tam 50), yeniden başlatınca 50/50 `Dispatching`, 51 job (ısınma siparişi dahil), outbox boşalıyor |
| 3 | Zehirli mesaj 3 denemeden sonra DLQ; arkasındakiler akıyor | ✅ | `Consume_PoisonMessage_IsRetriedThreeTimesThenParkedInDlq_…`: DLQ'daki mesajda `x-attempt=4` (1 + 3 yeniden deneme); sıradaki mesaj, poison henüz DLQ'ya varmadan işleniyor; tüm retry kuyrukları ve ana kuyruk boş |
| 4 | POST → relay → Dispatch → Orders zinciri dashboard'da **tek trace** | ✅ | `RoundTrip_…_AsOneTrace` (süreç içi span yakalama) + dashboard'ın telemetri API'sinden okunan gerçek iz: tek `traceId` altında HTTP isteği + 5 mesajlaşma span'i (`dashboard-trace.txt`). Servis adları testte tek süreç olduğu için birleşik görünür (aşağıya bkz.) |
| 5 | Contract snapshot'ları; alan silmek testi kırıyor | ✅ | `Rota.Contracts.Tests` (6 olay × snapshot + 2 koruma testi). `OrderCreatedV1.DistanceMeters` silinince test kırıldı (1/8 failed), geri alınca geçti |

## Testlerin gerçekten bir şey kanıtladığının kontrolü (mutasyon)

- **Inbox kaldırıldı** (`ConsumerService.ProcessAsync` içindeki `InboxMessage` ekleme satırı silindi): `Consume_SameEventDeliveredTwice_…` **kırıldı**; satır geri konunca geçti.
- **Sözleşme alanı silindi** (yukarıda): snapshot testi kırıldı.
- İlk deneme geçersizdi (derlenmedi, eski derlemeyle koştu); fark edilip düzgün yapıldı.

Mesajlaşma testleri 3 ardışık koşuda da 8/8 geçti (flaky gözlenmedi); CI'da henüz koşmadı.

## FAIL sinyalleri

- `autoAck: true`: yok. ACK her zaman commit'ten sonra (`ConsumerService.cs`: `CommitAsync` satır 206, normal yol ACK'i satır 164).
- Doğrudan `BasicPublishAsync`: **iki yerde**. (1) `OutboxRelay`: olayların broker'a girdiği tek yer. (2) `ConsumerService.MoveAsync`: zaten yayınlanmış mesajı retry kuyruğuna / DLQ'ya taşımak (varsayılan exchange, `mandatory`); yeni olay yayınlamaz, ama roadmap'in "outbox dışı doğrudan publish" ifadesinin kapsamına girip girmediği Salih'in yorumuna bağlı.

## Dashboard doğrulaması (kriter 4)

İz, dashboard'ın `/api/telemetry/traces` ucundan okundu (`dashboard-trace.txt`); ekran görüntüsü alınmadı. Elle bakmak istersen:

Round-trip testi OTLP export açıkken koşturuldu (`OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`), iz compose'taki Aspire Dashboard'a gönderildi. Bakmak için: <http://localhost:18888> → Traces → `Rota.Orders.Api` / `Rota.Dispatch.Api` kökenli `POST /api/v1/orders` izi; içinde `orders.order-created.v1 publish`, `q.dispatch.order-created process`, `dispatch.dispatch-started.v1 publish`, `q.orders.dispatch-started process` span'leri tek izde görünmeli. Komut:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 dotnet test tests/Rota.Orders.IntegrationTests --filter-method "*RoundTrip_OrderCreatedThroughDispatch*"
```

## Yapılmayanlar / bilinen sınırlar

- Dashboard'da servis adlarının ayrı ayrı görünmesi (Orders / Dispatch) doğrulanmadı: test iki servisi tek süreçte çalıştırır, hepsi `Rota.Orders.Api` altında birleşik görünür. İki ayrı süreçle doğrulama, servisler yerelde başlatılabilince yapılır.
- Tüketicisi henüz kuyruk açmamış olay `q.rota.unrouted`'a düşer ve sonradan otomatik teslim edilmez (05 §3 "Bilinen sınır"). Bu bilinçli bir takas; test bunu belgeliyor (`Publish_EventWithNoConsumerQueue_…`).
- Yerel makinede servisler hâlâ başlatılamıyor (F02 raporundaki PostgreSQL port çakışması); testler Testcontainers kullandığı için etkilenmedi.
- Inbox satırları silinmiyor (belge yalnızca outbox için 7 günlük temizlik tanımlıyor).
- Birden çok relay örneği senaryosu (`SKIP LOCKED` ile iş paylaşımı) test edilmedi; mekanizma kodda var ama iki örnekle yarışan bir test yok.
- Retry gecikmesi değiştirilirse mevcut broker'daki retry kuyrukları silinmeli (RabbitMQ aynı kuyruğu farklı argümanla yeniden tanımlamayı reddeder).
- Dispatch servisinde henüz HTTP ucu / kimlik doğrulama yok (F05).
