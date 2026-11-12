# 05 — Sözleşmeler (REST, gRPC, olaylar, webhook, GraphQL)

Bu belgedeki şekiller **taslaktır**; ilgili fazda koda dönüşürken netleşir. Yayınlandıktan sonra (faz PR'ı birleşince) sözleşmeyi kırmak yasaktır: yeni alan eklenebilir, alan silinemez/anlamı değişemez; kırıcı değişiklik = yeni sürüm (`v2`).

## 1. REST

Genel kurallar:
- Önek `/api/v1`. JSON alan adları `camelCase`. Zamanlar ISO-8601 UTC (`2026-11-11T10:31:00Z`). Mesafe metre, süre saniye (alan adında birim: `distanceMeters`, `etaSeconds`).
- Hatalar `application/problem+json` (RFC 9457 ProblemDetails); `type` alanı hata kataloğundaki kimliktir (bkz. [06](06-security-and-errors.md)). Her yanıtta `traceId`.
- Listeleme: cursor pagination — `?limit=50&after=<opaque cursor>` → `{ items: [...], nextCursor: "..." | null }`. Offset yok.
- Sahiplik kuralı: kaynak çağırana ait değilse **404** (403 değil) — var olup olmadığı sızdırılmaz. 403 yalnızca rol eksikliğinde.
- Her serviste `GET /health/live` ve `GET /health/ready`.

### Orders

| Uç | Rol | Başarı | Hatalar |
|---|---|---|---|
| `POST /api/v1/orders` (başlık `Idempotency-Key` zorunlu) | merchant | 201 | 400 doğrulama / eksik anahtar · 409 `idempotency-key-reused` · 409 `duplicate-merchant-order-ref` · 422 `out-of-service-area` · 429 |
| `GET /api/v1/orders/{id}` | sahibi merchant · dispatcher · atanmış kurye | 200 | 404 |
| `GET /api/v1/orders?status=&limit=&after=` | merchant (kendi) · dispatcher (hepsi) | 200 | 400 |
| `POST /api/v1/orders/{id}/cancel` | sahibi merchant · dispatcher | 200 | 404 · 409 `invalid-order-transition` |
| `POST /api/v1/orders/{id}/pickup` | atanmış kurye | 200 | 404 · 409 |
| `POST /api/v1/orders/{id}/deliver` | atanmış kurye | 200 | 400 `invalid-delivery-code` (+ `remainingAttempts`) · 404 · 409 `delivery-code-locked` · 409 |
| `POST /api/v1/orders/{id}/delivery-failed` | atanmış kurye | 200 | 404 · 409 |
| `POST /api/v1/orders/{id}/returned` | atanmış kurye · dispatcher | 200 | 404 · 409 |
| `POST /api/v1/orders/{id}/delivery-code/unlock` | dispatcher | 200 | 404 · 409 |
| `GET /.well-known/tracking-jwks` | iç ağ | 200 | — |

Örnek `POST /api/v1/orders` gövdesi ve yanıtı:

```json
{
  "merchantOrderRef": "PZ-2026-000123",
  "pickup":  { "lat": 40.9903, "lon": 29.0290, "address": "Kadıköy Şube, Moda Cd. 10" },
  "dropoff": { "lat": 40.9850, "lon": 29.0400, "address": "Caferağa Mh. ..." },
  "customer": { "name": "Ayşe Y.", "phone": "+905551112233" }
}
```

```json
{
  "id": "01933c5e-...",
  "status": "Created",
  "distanceMeters": 1120,
  "deliveryCode": "4821",
  "trackingUrl": "https://rota.local/track/<trackingToken>",
  "orderVersion": 1,
  "createdAt": "2026-11-11T10:31:00Z"
}
```

> `deliveryCode` yalnızca bu yanıtta döner (DB'de hash'i var). İşletme kodu müşteriye kendi kanalından (SMS vb.) iletir.
>
- `GET /api/v1/orders/{id}` ve `cancel` yanıtı: `{ id, merchantOrderRef, status, pickup{lat,lon,address}, dropoff{...}, customer{name,phone}, distanceMeters, assignedCourierId, deliveryCodeLocked, orderVersion, createdAt, updatedAt }`. Teslim kodu ve hash'i hiçbir okuma yanıtında yoktur.
- `GET /api/v1/orders` yanıtı: `{ items: [...], nextCursor }`, en yeni önce. `limit` 1–100 (varsayılan 20), `after` = bir önceki sayfanın `nextCursor`'ı (opak), `status` = `OrderStatus` adı (büyük/küçük harf duyarsız). Geçersiz değer → 400 `validation-failed` (`errors` içinde parametre adı).
- `POST /api/v1/orders/{id}/cancel` gövdesi isteğe bağlı: `{ "reason": "..." }` (≤ 300). Eşzamanlı değişiklik → 409 `concurrency-conflict`. Zaten iptal edilmiş sipariş → 409 `invalid-order-transition` (`currentStatus: Cancelled`); istemci bunu "iptal zaten gerçekleşti" diye yorumlayabilir.

> Aynı `Idempotency-Key` ile tekrar gelen istek aynı gövdeyi alır; bunun için yanıt `idempotency_keys.response_body`'de saklanır. İçindeki sırlar (`deliveryCode`, `trackingUrl`) orada **AES-GCM ile şifrelidir** (sunucu anahtarı, ek doğrulama verisi = sipariş id'si); düz metin kod DB'ye hiç yazılmaz (F02 kararı).

### Dispatch

| Uç | Rol | Başarı | Hatalar |
|---|---|---|---|
| `POST /api/v1/couriers/me/shift/start` | courier | 200 | 409 (zaten açık) |
| `POST /api/v1/couriers/me/shift/end` | courier | 200 | 409 `courier-busy` |
| `GET /api/v1/couriers/me/offers` | courier | 200 (bekleyen teklif veya boş) | — |
| `POST /api/v1/offers/{id}/accept` | teklifin kuryesi | 200 | 404 · 409 `offer-not-pending` (süresi dolmuş/iptal) |
| `POST /api/v1/offers/{id}/reject` | teklifin kuryesi | 200 | 404 · 409 |
| `GET /api/v1/couriers?state=` | dispatcher | 200 | — |
| `POST /api/v1/dispatch-jobs/{orderId}/manual-assign` `{ "courierId": "..." }` | dispatcher | 200 | 404 · 409 `courier-not-available` · 409 `job-not-assignable` |
| `POST /api/v1/dispatch-jobs/{orderId}/restart` | dispatcher | 202 | 404 · 409 |

### Notifications

| Uç | Rol | Başarı | Hatalar |
|---|---|---|---|
| `PUT /api/v1/webhook-subscription` `{ "url": "https://..." }` | merchant | 200 (`secret` yalnız ilk oluşturmada döner) | 400 (https değil, özel IP) |
| `POST /api/v1/webhook-subscription/rotate-secret` | merchant | 200 (yeni `secret`) | 404 |
| `GET /api/v1/webhook-deliveries?status=Dead&limit=&after=` | merchant (kendi) | 200 | — |
| `POST /api/v1/webhook-deliveries/{id}/replay` | merchant (kendi) | 202 | 404 · 409 (zaten başarılı) |

## 2. gRPC

Proto dosyaları `src/Contracts/Rota.Contracts.Protos/` altında. Paket adları sürüm içerir (`rota.tracking.v1`). **Kurye kimliği mesajda değil, token'dan alınır** — bir kurye başka kurye adına konum veya teklif akışı açamaz.

```proto
syntax = "proto3";
package rota.tracking.v1;
option csharp_namespace = "Rota.Contracts.Tracking.V1";
import "google/protobuf/timestamp.proto";

// Kurye → Tracking (mobil, gateway üzerinden)
service CourierLocationIngest {
  rpc StreamLocations (stream LocationPoint) returns (StreamLocationsResult);
}

// Dispatch / Ops API → Tracking (iç ağ)
service CourierLocationQuery {
  rpc GetNearbyCouriers (GetNearbyCouriersRequest) returns (GetNearbyCouriersResponse);
  rpc GetLastLocations  (GetLastLocationsRequest)  returns (GetLastLocationsResponse);   // Ops API DataLoader'ı için toplu
}

message LocationPoint {
  int64 seq = 1;                                   // kurye cihazında artan sıra numarası
  double lat = 2;
  double lon = 3;
  float accuracy_m = 4;
  google.protobuf.Timestamp recorded_at = 5;
}
message StreamLocationsResult { int64 received = 1; int64 accepted = 2; int64 out_of_order = 3; }

message GetNearbyCouriersRequest { double lat = 1; double lon = 2; int32 radius_m = 3; int32 limit = 4; int32 max_age_seconds = 5; }
message NearbyCourier { string courier_id = 1; int32 distance_m = 2; google.protobuf.Timestamp last_seen_at = 3; }
message GetNearbyCouriersResponse { repeated NearbyCourier couriers = 1; }

message GetLastLocationsRequest { repeated string courier_ids = 1; }
message CourierLocation { string courier_id = 1; double lat = 2; double lon = 3; google.protobuf.Timestamp recorded_at = 4; }
message GetLastLocationsResponse { repeated CourierLocation locations = 1; }
```

```proto
syntax = "proto3";
package rota.dispatch.v1;
option csharp_namespace = "Rota.Contracts.Dispatch.V1";
import "google/protobuf/timestamp.proto";

// Dispatch → Kurye (server-streaming; kurye bağlantıyı açık tutar)
service CourierOffers {
  rpc SubscribeOffers (SubscribeOffersRequest) returns (stream OfferEvent);
}

message SubscribeOffersRequest {}
message GeoPoint { double lat = 1; double lon = 2; string address = 3; }
message OfferEvent {
  oneof event {
    Offer offer = 1;               // yeni teklif (bağlanınca bekleyen teklif de önce bu şekilde gelir)
    OfferWithdrawn withdrawn = 2;  // sipariş iptal / teklif süresi doldu
    Heartbeat heartbeat = 3;       // 15 sn'de bir; ara katmanların boşta bağlantıyı kesmesini önler
  }
}
message Offer {
  string offer_id = 1; string order_id = 2;
  GeoPoint pickup = 3; GeoPoint dropoff = 4;
  int32 distance_m = 5; int32 eta_seconds = 6;
  google.protobuf.Timestamp expires_at = 7;
}
message OfferWithdrawn { string offer_id = 1; string reason = 2; }
message Heartbeat { google.protobuf.Timestamp server_time = 1; }
```

### Tracking ayrıntıları (F04)

- **`StreamLocations`:** her nokta doğrulanır (`lat` ±85,05112878 aralığı: Redis GEO bunun ötesini reddeder; `lon` ±180; `seq` ≥ 1; `recorded_at` zorunlu ve kabul penceresi içinde, bkz. 04). Geçersiz nokta çağrıyı `INVALID_ARGUMENT` ile **sonlandırır**; öncesindeki noktalar kalır.
- **Sonuç sayıları:** `received` = okunan, `accepted` = anlık konum olan, `out_of_order` = mevcuttan **daha yeni olmayan** (eski ya da aynı `seq`). `out_of_order` noktalar da geçmişe yazılır; tekrarlar `ON CONFLICT DO NOTHING` ile tek satır olur.
- **`GetNearbyCouriers`:** `radius_m` zorunlu (1–50.000); `limit` 0 = 20, en çok 100; `max_age_seconds` 0 = 30, en çok 300. Arama ve tazelik filtresi tek script'tedir ve **yeterli taze kurye bulunana kadar pencereyi genişletir**: bayat ama yakın kuryeler `limit` kontenjanını doldurup biraz uzaktaki taze kuryeleri gizlemez. Sonuçlar mesafeye göre artan sıradadır.
- **`GetLastLocations`:** en çok 1.000 kimlik; geçersiz GUID tüm çağrıyı reddeder; bilinmeyen kurye sonuçta yer almaz. `recorded_at` **cihaz saatidir**.
- **Yetki:** `StreamLocations` yalnız `courier` rolü (kimlik token'dan, mesajdan değil); `GetNearbyCouriers` ve `GetLastLocations` yalnız `service` rolü (Dispatch, Ops API): bir kurye diğer kuryelerin yerini soramaz. Rol dışı → `PERMISSION_DENIED`, kimlik yok → `UNAUTHENTICATED`.
- **Portlar (yerel):** HTTP/1.1 `5103` (health), gRPC HTTP/2 `5203` (TLS'siz yerelde ayrı port).

### Dispatch ayrıntıları (F05)

- **Portlar (yerel):** HTTP/1.1 `5102` (REST, health), gRPC HTTP/2 `5202`.
- **`POST /couriers/me/shift/start`:** gövde isteğe bağlı `{ "displayName": "..." }` (≤ 100). Kurye ilk vardiyada kendini kaydeder (04). Zaten `Available`/başka durumdaysa 409 `invalid-courier-transition` (`currentState` eklenir). Aynı kurye aynı anda birden çok kez başlatırsa biri 200, diğerleri 409 alır.
- **`POST /offers/{id}/accept|reject`:** teklif başkasınınsa ya da yoksa **404** (ayrım yok). Süresi dolmuş ama zaman aşımı işi henüz kapatmamış teklif de 409 `offer-not-pending` verir: kabul/ret koşulu `expires_at > şimdi`'dir. Yanıt `{ offerId, orderId, status }`.
- **`GET /couriers/me/offers`:** bekleyen teklif listesi (0 ya da 1 eleman): `id, orderId, pickup{lat,lon,address}, dropoff{...}, distanceMeters, etaSeconds, expiresAt`.
- **`POST /dispatch-jobs/{orderId}/manual-assign`:** yalnız `Searching` ya da `Unassigned` iş atanabilir; açık teklif varken (`Offering`) ve biten işte 409 `job-not-assignable`. Kurye `Available` değilse 409 `courier-not-available` (`currentState`), yoksa 404; her ikisinde işin durumu **değişmez** (aynı transaction'da geri alınır). Yayınlanan `CourierAssigned.v1`: `assignmentType: Manual`, `offerId: null`, `etaSeconds: 0` (rota hesaplanmadı).
- **`POST /dispatch-jobs/{orderId}/restart`:** yalnız `Unassigned` iş; 202. Aynı iş yeniden `Searching` olur (bkz. `02 §5.4` notu) ve `DispatchStarted.v1` yeniden yayınlanır (Orders: `Unassigned → Dispatching`).
- **`SubscribeOffers`:** bağlanınca bekleyen teklif **ilk** olay olarak gelir. `withdrawn.reason`: `order-cancelled` ya da `expired`; kuryenin kendi kabul/reddi için `withdrawn` gönderilmez. Kalp atışı `StreamHeartbeat` (15 sn). Aynı kurye birden çok akış açabilirse hepsi aynı teklifi alır (yeniden bağlanma anında eski akış kapanmadan yenisi açılabilir). Kimlik token'dan: rol `courier` değilse `PERMISSION_DENIED`, kimlik yoksa `UNAUTHENTICATED`.
- **Olaylar:** `DispatchFailed.v1.reason` = `NoCandidates` (3 dk içinde kimse bulunamadı) ya da `MaxAttemptsReached` (5 teklif düştü). `CourierStateChanged.v1.courierVersion` = `couriers.version`. İş düzeyindeki olaylarda (`DispatchStarted`, `CourierAssigned`, `DispatchFailed`) `aggregateVersion` sabit 1'dir: Orders bu olayları sürümle değil durum makinesiyle eler (geçersiz geçiş → yok say, ACK).
- **Dispatch'in kuyrukları:** `q.dispatch.order-created` (`OrderCreated`), `q.dispatch.order-status-changed` (`OrderStatusChanged`: `Cancelled` → iş/teklif iptal + kurye serbest; `Delivered`/`Returned` → kurye `Available`). Orders'ınkiler: `q.orders.dispatch-started`, `q.orders.courier-assigned`, `q.orders.dispatch-failed`. İptal olayı, siparişin kendi `OrderCreated`'ını geçerse (farklı kuyruklar) iş henüz yoktur; tüketici hata verip yeniden denemeye bırakır (05 §3 yeniden deneme kuyrukları), "ACK edip unutmaz".
- **Tracking istemcisi:** `GetNearbyCouriers` için tüm çağrıya (yeniden deneme dahil) 200 ms deadline; kanal düzeyinde 1 yeniden deneme yalnız `UNAVAILABLE`'da (gRPC `ServiceConfig`). `DEADLINE_EXCEEDED`/`UNAVAILABLE` sonrası iş `Searching`'de kalır, `next_search_at` 20 sn sonraya konur (deneme sayısı artmaz). Diğer gRPC hataları (ör. `INVALID_ARGUMENT`) yeniden denenmez ve bir hata olarak yukarı çıkar.

### Teslimat uçları (F06)

- **Erişim:** `pickup`, `deliver`, `delivery-failed` yalnız `courier` rolü (403 diğerleri); `returned` kurye ya da dispatcher; `delivery-code/unlock` yalnız dispatcher. Kurye yalnız **kendine atanmış** siparişi görür: başkasının ya da henüz kimseye atanmamış siparişi **404** (varlığı sızdırılmaz). Rol ihlali 403, kimlik yoksa 401.
- **Nihai tutarlılık:** Kurye teklifi kabul ettiği anda Dispatch hemen 200 döner, ama Orders "bu kurye bu siparişe atandı" bilgisini `CourierAssigned` olayından bir an sonra öğrenir. O ana kadar sipariş kuryeye görünmez ve `pickup` **404** verir. İstemci birkaç saniye, kısa aralıkla yeniden denemelidir (simülatör 500 ms × 20). Sipariş atanınca `pickup` 200 olur. 404 dışındaki cevap (ör. 409) nihaidir.
- **`deliver`** gövdesi `{ "code": "4821" }` (gövde yoksa ya da kod 4 rakam değilse 400 `validation-failed`; **bu bir deneme sayılmaz**). Önce durum kontrol edilir: `PickedUp` değilse 409 `invalid-order-transition`, kod hiç denenmez, deneme yanmaz. Yanlış kod 400 `invalid-delivery-code` + `remainingAttempts`; **5. yanlışta kilitlenir** (cevap yine 400, `remainingAttempts: 0`). Kilitliyken doğru kod bile 409 `delivery-code-locked`. Dispatcher `delivery-code/unlock` ile deneme sayısını sıfırlar (kilitli değilken de 200: tekrar edilebilir; yalnız `PickedUp` iken, aksi 409).
- **Yanlış kodun kaydı:** başarısız deneme de veritabanına yazılır (yoksa sınır hiç dolmaz). Aynı anda gelen birden çok yanlış tahminden yalnız `xmin` koşulunu geçenler yazılır, kalanı 409 `concurrency-conflict` alır: saklanan sayı, "400" cevabı verilen tahmin sayısına eşittir ve 5'i geçmez.
- **Yarış (R-4):** iptal ile `pickup` aynı anda gelirse ikisi de `Assigned`'ı okur ve ikisi de geçerlidir; `UPDATE ... WHERE xmin = @okunan` yalnız birini geçirir: biri 200, öteki 409. Kaybeden asla 500 almaz, kazananın değişikliği için tam bir `OrderStatusChanged` olayı yazılır.
- **Olaylar:** `pickup`, `deliver`, `delivery-failed`, `returned` her biri `OrderStatusChanged.v1` yazar (durum değişimiyle aynı transaction'da; `delivery-failed` için `reason` taşınır). `unlock` durum değiştirmez, olay yoktur. `Delivered` ve `Returned` Dispatch'te kuryeyi `Available` yapar (teslimden sonra ≤ 2 sn, testli); `delivery-failed`'da paket hâlâ kuryededir, kurye `returned`'e kadar meşgul kalır.

### gRPC durum kodları

| Durum | Kod |
|---|---|
| Geçersiz koordinat / limit | `INVALID_ARGUMENT` |
| Token yok / geçersiz | `UNAUTHENTICATED` |
| Rol yok | `PERMISSION_DENIED` |
| Deadline aşıldı (çağıran tarafında) | `DEADLINE_EXCEEDED` |
| Redis/DB geçici olarak erişilemez | `UNAVAILABLE` (çağıran retry edebilir) |
| Beklenmeyen hata | `INTERNAL` (ayrıntı loga, istemciye değil) |

| Çağrı | Deadline | Retry |
|---|---|---|
| Dispatch → `GetNearbyCouriers` | 200 ms | 1 kez, yalnız `UNAVAILABLE` |
| Ops API → `GetLastLocations` | 300 ms | yok (konum gösterilmez, sorgu yine döner) |

## 3. Olaylar (RabbitMQ)

- Exchange: `rota.events` (topic, durable). Routing key: `<servis>.<olay-adı>.v<sürüm>`.
- Kuyruklar quorum, tüketici başına bir kuyruk; mesajlar kalıcı (persistent); publisher confirm açık.
- Başlıklar: `message-id` (= eventId), `type`, `traceparent`, `content-type: application/json`.
- Tüketici hatası → 3 yeniden deneme (ilk çalıştırma + 3 = en çok 4 handler çalışması). Bekleme kuyrukları `<kuyruk>.retry.1/2/3` (kuyruk düzeyinde TTL: 5 sn, 30 sn, 2 dk; süre dolunca mesaj dead-letter ile asıl kuyruğa döner) → son denemeden sonra `<kuyruk>.dlq`. Tüm kuyruklar quorum.
- Deneme sayısı `x-attempt` başlığında (ilk teslimde yok = 1). DLQ'daki mesajda ayrıca `x-error` (kesilmiş hata metni) ve `x-source-queue` bulunur. Bozuk JSON / yanlış `type` yeniden denenmez, doğrudan DLQ'ya gider.
- Sıra: tüketici önce transaction açar, `inbox_messages`'a (eventId, tüketici) yazar, iş değişikliğini yapar, **commit eder, sonra ACK eder**. Inbox PK çakışması = mesaj daha önce işlendi → iş yapılmaz, ACK edilir. `autoAck` kullanılmaz.
- Yayın tarafı en-az-bir-kez (at-least-once): relay, satırı yalnızca broker publisher confirm verdikten sonra `processed_at` ile işaretler; confirm ile commit arasında çökerse olay tekrar yayınlanır ve tüketici tarafındaki inbox mükerreri yutar. Birden çok relay örneğinde sıra garantisi yoktur; tüketiciler `aggregateVersion` ile eskiyi eler.
- Kuyruğu **tüketici** tanımlar (başlangıçta, idempotent declare). `rota.events` exchange'inin alternate exchange'i `rota.unrouted` → `q.rota.unrouted` (7 gün / 100 bin mesaj sınırlı park kuyruğu): hiçbir kuyruğa uymayan olay kaybolmaz, burada beklemeye alınır.

### Zarf (envelope)

```json
{
  "eventId": "01933c5e-...",
  "type": "OrderStatusChanged.v1",
  "occurredAt": "2026-11-11T10:31:05Z",
  "aggregateId": "01933c5e-...",
  "aggregateVersion": 4,
  "data": { }
}
```

> **Bilinen sınır (F03):** Bir olay, tüketicisinin kuyruğu **ilk kez** tanımlanmadan yayınlanırsa `q.rota.unrouted`'a düşer ve tüketici sonradan açılsa da ona **otomatik teslim edilmez** (elle yeniden oynatma gerekir). Bu yalnızca ilk kurulum penceresini etkiler (kuyruklar kalıcı). Önlem: dağıtımda tüketici servisleri üreticilerden önce başlatılır; F12'de compose `depends_on` ile yazılır. Henüz tüketicisi olmayan olaylar (ör. `OrderStatusChanged`, Notifications/Ops API gelene kadar) bu yüzden `q.rota.unrouted`'da birikir.

### Olay kataloğu

| Olay | Routing key | Üretici | Tüketiciler | `data` |
|---|---|---|---|---|
| `OrderCreated.v1` | `orders.order-created.v1` | Orders | Dispatch, Ops API | `orderId, merchantId, merchantOrderRef, pickup{lat,lon,address}, dropoff{lat,lon,address}, distanceMeters, createdAt, orderVersion` |
| `OrderStatusChanged.v1` | `orders.order-status-changed.v1` | Orders | Dispatch, Notifications, Ops API | `orderId, merchantId, merchantOrderRef, fromStatus, toStatus, courierId?, reason?, occurredAt, orderVersion` |
| `DispatchStarted.v1` | `dispatch.dispatch-started.v1` | Dispatch | Orders | `orderId, startedAt` |
| `CourierAssigned.v1` | `dispatch.courier-assigned.v1` | Dispatch | Orders | `orderId, courierId, offerId?, assignmentType (Auto/Manual), etaSeconds, assignedAt` |
| `DispatchFailed.v1` | `dispatch.dispatch-failed.v1` | Dispatch | Orders | `orderId, reason (NoCandidates/MaxAttemptsReached), failedAt` |
| `CourierStateChanged.v1` | `dispatch.courier-state-changed.v1` | Dispatch | Ops API | `courierId, displayName, state, currentOrderId?, occurredAt, courierVersion` |

**Kim neye tepki verir?**
- Orders: `DispatchStarted` → `Created/Unassigned → Dispatching`; `CourierAssigned` → `Assigned`; `DispatchFailed` → `Unassigned`. Her geçiş sonrası `OrderStatusChanged.v1` yayınlar. Geçersiz geçiş (ör. iptal edilmiş sipariş) → olay yok sayılır, ACK edilir, metrik artar.
- Dispatch: `OrderStatusChanged` → `Cancelled` ise iş ve teklif iptal, kurye serbest; `Delivered`/`Returned` ise kurye `Available`.
- Notifications: yalnızca `OrderStatusChanged` (işletmeye giden gerçeğin tek kaynağı Orders'tır).

**Neden `OrderStatusChanged` tek olay?** Her geçiş için ayrı olay (OrderPickedUp, OrderDelivered...) daha açıklayıcıdır ama tüketicilerin hepsi "durum değişti, yeni durum X" ile aynı işi yapıyor. Tek olay + `toStatus` sözleşme sayısını azaltır; `OrderCreated` ise taşıdığı veri farklı olduğu için ayrı. Bkz. ADR-011.

## 4. Webhook (Rota → İşletme)

Başlıklar:
```
Content-Type: application/json
Rota-Event-Id: 01933c5e-...
Rota-Event-Type: order.delivered
Rota-Signature: t=1762857065,v1=5f2b...e9
```

- İmza: `HMAC-SHA256(secret, "{t}.{ham gövde}")`, hex. Alıcı: `t` 5 dakikadan eskiyse reddeder (replay saldırısı), sabit zamanlı karşılaştırma (`CryptographicOperations.FixedTimeEquals`) yapar.
- Gövde:

```json
{
  "id": "01933c5e-...",
  "type": "order.delivered",
  "createdAt": "2026-11-11T10:55:12Z",
  "data": {
    "orderId": "01933c5e-...",
    "merchantOrderRef": "PZ-2026-000123",
    "status": "Delivered",
    "orderVersion": 7,
    "courier": { "id": "…", "displayName": "Mehmet K." },
    "reason": null
  }
}
```

| `toStatus` | Webhook `type` |
|---|---|
| Assigned | `order.assigned` |
| Unassigned | `order.unassigned` |
| PickedUp | `order.picked_up` |
| Delivered | `order.delivered` |
| DeliveryFailed | `order.delivery_failed` |
| Returned | `order.returned` |
| Cancelled | `order.cancelled` |

Garantiler (README'de işletmeye açıkça yazılır): **en az bir kez** teslim (aynı `id` tekrar gelebilir → ayıklayın), **sıra garantisi yok** (`orderVersion` küçükse eski olaydır → yok sayın), 2xx dışındaki her yanıt ve 10 sn'yi aşan bekleme başarısız sayılır.

## 5. GraphQL (Ops API)

```graphql
type Query {
  orders(where: OrderFilterInput, order: [OrderSortInput!], first: Int, after: String): OrdersConnection   # dispatcher
  order(id: UUID!): Order                                                                                 # dispatcher
  couriers(state: CourierState): [Courier!]!                                                              # dispatcher
  dispatchOverview: DispatchOverview!                                                                     # dispatcher
}

type Order {
  id: UUID!
  merchantId: String!
  merchantOrderRef: String!
  status: OrderStatus!
  courier: Courier                 # DataLoader (courier_view)
  pickup: GeoPoint!
  dropoff: GeoPoint!
  createdAt: DateTime!
  assignedAt: DateTime
  deliveredAt: DateTime
  timeToAssignSeconds: Int         # hesaplanan alan
}

type Courier {
  id: UUID!
  displayName: String!
  state: CourierState!
  currentOrder: Order              # DataLoader (order_view)
  lastLocation: CourierLocation    # DataLoader → Tracking.GetLastLocations (tek gRPC çağrısı)
}

type DispatchOverview { createdOrders: Int! unassignedOrders: Int! activeDeliveries: Int! availableCouriers: Int! }

type Mutation {
  assignCourier(input: AssignCourierInput!): AssignCourierPayload!     # → Dispatch REST
  restartDispatch(input: RestartDispatchInput!): RestartDispatchPayload!
}
# Hata tipleri (mutation conventions): CourierNotAvailableError, JobNotAssignableError, OrderNotFoundError

type Subscription {
  courierLocationUpdated(courierIds: [UUID!]): CourierLocation!   # dispatcher
  orderStatusChanged: OrderStatusChangedEvent!                    # dispatcher
  orderTracking(trackingToken: String!): TrackingUpdate!          # anonim; token doğrulanır (I-9)
}
```

- **N+1:** `orders { courier { lastLocation } }` sorgusu 50 sipariş için 1 + 1 (courier_view) + 1 (gRPC) çağrı yapmalı, 101 değil. Testte sorgu sayısı sabitlenir.
- **Maliyet sınırı:** Hot Chocolate cost analysis açık; azami maliyet ve derinlik sınırı aşılırsa sorgu çalıştırılmadan reddedilir.
- **Çok instance'lı subscription:** `orderStatusChanged` olayı RabbitMQ'dan Ops API instance'larından **yalnız birine** düşer (rakip tüketiciler). Diğer instance'lara bağlı panellerin de görmesi için Hot Chocolate subscription altyapısı **Redis** sağlayıcısıyla çalışır. Konum yayını ise her instance'ın Tracking kanalına kendisi abone olmasıyla dağılır.
