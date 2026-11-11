# 04 — Veri modeli

Genel kurallar:
- Her servisin **ayrı veritabanı** vardır; servisler arası foreign key yoktur. Başka servise ait kimlikler (`courier_id` Orders'ta gibi) yalnızca değer olarak tutulur.
- Kimlikler `uuid` (uygulamada `Guid.CreateVersion7()` — zamana göre sıralı, indeks dostu).
- Zamanlar `timestamptz`, değer her zaman UTC.
- Optimistic concurrency için PostgreSQL'in sistem sütunu `xmin` kullanılır (EF Core'da `uint Version` + `IsRowVersion()`).
- Tablo ve sütun adları `snake_case`.
- Her serviste ortak tablolar: `outbox_messages`, `inbox_messages` (aşağıda bir kez tanımlı).

## Ortak: outbox ve inbox

```sql
CREATE TABLE outbox_messages (
    id              uuid PRIMARY KEY,           -- aynı zamanda olayın eventId'si
    type            text        NOT NULL,       -- 'OrderCreated.v1'
    routing_key     text        NOT NULL,       -- 'orders.order-created.v1'
    payload         jsonb       NOT NULL,       -- olay zarfı
    trace_parent    text        NULL,           -- W3C trace bağlamı
    occurred_at     timestamptz NOT NULL,
    processed_at    timestamptz NULL,
    attempt_count   int         NOT NULL DEFAULT 0,
    last_error      text        NULL
);
CREATE INDEX ix_outbox_unprocessed ON outbox_messages (occurred_at) WHERE processed_at IS NULL;

CREATE TABLE inbox_messages (
    message_id      uuid        NOT NULL,       -- gelen olayın eventId'si
    consumer        text        NOT NULL,       -- 'Dispatch.OrderCreatedConsumer'
    processed_at    timestamptz NOT NULL,
    PRIMARY KEY (message_id, consumer)
);
```

- **Outbox:** İş verisiyle aynı transaction'da yazılır. Relay `FOR UPDATE SKIP LOCKED` ile alır, yayınlar, `processed_at` doldurur. İşlenmiş satırlar 7 gün sonra silinir.
- **Inbox:** Tüketici, iş değişikliğiyle **aynı transaction'da** `inbox_messages`'a yazar. PK çakışırsa mesaj daha önce işlenmiştir → ACK edip geç.

## Orders (`rota_orders`)

```sql
CREATE TABLE orders (
    id                      uuid PRIMARY KEY,
    merchant_id             text        NOT NULL,
    merchant_order_ref      text        NOT NULL,          -- işletmenin kendi sipariş numarası
    status                  text        NOT NULL,          -- OrderStatus
    pickup_lat              double precision NOT NULL,
    pickup_lon              double precision NOT NULL,
    pickup_address          text        NOT NULL,
    dropoff_lat             double precision NOT NULL,
    dropoff_lon             double precision NOT NULL,
    dropoff_address         text        NOT NULL,
    customer_name           text        NOT NULL,          -- kişisel veri
    customer_phone          text        NOT NULL,          -- kişisel veri, loglarda maskeli
    distance_meters         int         NOT NULL,
    delivery_code_hash      text        NOT NULL,          -- düz metin saklanmaz
    delivery_code_attempts  int         NOT NULL DEFAULT 0,
    delivery_code_locked    boolean     NOT NULL DEFAULT false,
    assigned_courier_id     uuid        NULL,              -- CourierAssigned olayından kopya
    version_number          int         NOT NULL DEFAULT 1,-- olaylarda orderVersion olarak yayınlanır
    created_at              timestamptz NOT NULL,
    updated_at              timestamptz NOT NULL,
    UNIQUE (merchant_id, merchant_order_ref)
);
CREATE INDEX ix_orders_merchant_created ON orders (merchant_id, created_at DESC, id DESC);
CREATE INDEX ix_orders_status ON orders (status) WHERE status IN ('Dispatching','Unassigned','Assigned','PickedUp');

CREATE TABLE order_status_history (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_id    uuid        NOT NULL REFERENCES orders(id),
    from_status text        NULL,
    to_status   text        NOT NULL,
    actor_type  text        NOT NULL,   -- Merchant, Courier, Dispatcher, System
    actor_id    text        NULL,
    reason      text        NULL,
    occurred_at timestamptz NOT NULL
);

CREATE TABLE idempotency_keys (
    merchant_id      text        NOT NULL,
    key              text        NOT NULL,
    request_hash     text        NOT NULL,   -- gövdenin SHA-256'sı: aynı anahtar + farklı gövde → 409
    response_status  int         NOT NULL,
    response_body    jsonb       NOT NULL,
    created_at       timestamptz NOT NULL,
    PRIMARY KEY (merchant_id, key)
);
-- 24 saatten eski anahtarlar temizlik işiyle silinir.
```

- Optimistic concurrency: `orders.xmin`. İptal ile teslim alma aynı anda gelirse biri `DbUpdateConcurrencyException` alır → 409.
- `version_number` iş seviyesindeki sürümdür (her durum değişikliğinde +1) ve olaylarla dışarı verilir; `xmin` ise yalnızca DB içi çakışma tespiti içindir.
- Cursor pagination `(created_at, id)` üzerinden yapılır (offset yok).

## Dispatch (`rota_dispatch`)

```sql
CREATE TABLE couriers (
    id                uuid PRIMARY KEY,          -- Keycloak kullanıcı kimliğiyle aynı
    display_name      text        NOT NULL,
    state             text        NOT NULL,      -- Offline, Available, Offered, OnDelivery
    current_offer_id  uuid        NULL,
    current_order_id  uuid        NULL,
    shift_started_at  timestamptz NULL,
    updated_at        timestamptz NOT NULL
);
CREATE INDEX ix_couriers_state ON couriers (state);

CREATE TABLE dispatch_jobs (
    order_id          uuid PRIMARY KEY,
    merchant_id       text        NOT NULL,
    status            text        NOT NULL,      -- Searching, Offering, Completed, Unassigned, Cancelled
    pickup_lat        double precision NOT NULL,
    pickup_lon        double precision NOT NULL,
    offer_attempts    int         NOT NULL DEFAULT 0,
    search_started_at timestamptz NOT NULL,
    next_search_at    timestamptz NULL,          -- aday yoksa yeniden arama zamanı
    updated_at        timestamptz NOT NULL
);
CREATE INDEX ix_jobs_next_search ON dispatch_jobs (next_search_at) WHERE status = 'Searching';

CREATE TABLE offers (
    id            uuid PRIMARY KEY,
    order_id      uuid        NOT NULL REFERENCES dispatch_jobs(order_id),
    courier_id    uuid        NOT NULL REFERENCES couriers(id),
    status        text        NOT NULL,          -- Pending, Accepted, Rejected, Expired, Cancelled
    distance_m    int         NOT NULL,
    eta_seconds   int         NOT NULL,
    created_at    timestamptz NOT NULL,
    expires_at    timestamptz NOT NULL,
    responded_at  timestamptz NULL
);
CREATE INDEX ix_offers_pending_expiry ON offers (expires_at) WHERE status = 'Pending';
CREATE UNIQUE INDEX ux_offers_one_pending_per_order ON offers (order_id) WHERE status = 'Pending';
CREATE UNIQUE INDEX ux_offers_one_pending_per_courier ON offers (courier_id) WHERE status = 'Pending';
```

- **I-1 ve I-2 iki katmanda korunur:** koşullu update (uygulama) + kısmi unique indeks (veritabanı). Uygulama kodunda hata olsa bile veritabanı ikinci `Pending` teklifi reddeder.
- Kısmi indeks (`WHERE status = 'Pending'`) yalnız bekleyen teklifleri içerir; tablo büyüse de zaman aşımı sorgusu küçük bir indekste çalışır.

## Tracking (`rota_tracking`)

```sql
CREATE TABLE location_points (
    courier_id   uuid             NOT NULL,
    seq          bigint           NOT NULL,
    lat          double precision NOT NULL,
    lon          double precision NOT NULL,
    accuracy_m   real             NULL,
    recorded_at  timestamptz      NOT NULL,   -- cihaz saati
    received_at  timestamptz      NOT NULL,   -- sunucu saati
    PRIMARY KEY (courier_id, seq, recorded_at)
) PARTITION BY RANGE (recorded_at);
-- Günlük bölümler (partition) bir arka plan işiyle önceden oluşturulur;
-- 30 günden eski bölümler DROP edilir (KVKK saklama süresi). DELETE yerine DROP: anında ve şişkinlik (bloat) bırakmaz.

CREATE UNLOGGED TABLE location_points_staging (LIKE location_points INCLUDING DEFAULTS);
```

- **Toplu yazma:** `COPY` (Npgsql binary import) → staging tablosu → `INSERT INTO location_points SELECT ... ON CONFLICT DO NOTHING`. `COPY` doğrudan `ON CONFLICT` desteklemediği için staging gerekir; tekrar gelen noktalar böylece sessizce elenir.
- Partition anahtarı birincil anahtarda bulunmak zorundadır; bu yüzden PK `(courier_id, seq, recorded_at)`.
- **Kabul penceresi (F04):** `recorded_at` sunucu saatinden en fazla **24 saat eski** ve en fazla **5 dakika ilerde** olabilir; aksi hâlde `INVALID_ARGUMENT`. Bölüm bakım işi dünden itibaren bölüm tuttuğu için pencerenin dışındaki bir nokta bölümsüz kalırdı. Bölümler: dün, bugün ve önümüzdeki 3 gün (UTC günleri).
- `accuracy_m`: protobuf'ta "0 = verilmedi"; 0 gelirse `NULL` yazılır.
- **Staging tablosu:** her toplu yazma tek transaction'dır (`COPY` → `INSERT ... ON CONFLICT DO NOTHING` → `DELETE FROM staging`). `TRUNCATE` kullanılmaz: ACCESS EXCLUSIVE kilidiyle birden çok Tracking örneğini birbirine bekletirdi. Bir transaction yalnız kendi commit edilmemiş staging satırlarını görür, commit'te tablo boştur.
- **Geçmiş en iyi çabadır:** DB 2 yeniden denemeden sonra (200 ms, 1 sn) erişilemezse o toplu yazma düşürülür ve `rota.tracking.history_dropped` sayılır; akışlar ve anlık konum (Redis) durmaz. Süreç çökerse en fazla 500 ms / 1.000 noktalık tampon kaybolur.
- F11'de EF Core `AddRange` ile `COPY` karşılaştırılıp ölçülecek.

### Tracking Redis anahtarları

| Anahtar | Tip | İçerik | Not |
|---|---|---|---|
| `trk:geo:couriers` | GEO (sorted set) | üye = courierId, konum | `GEOSEARCH ... BYRADIUS 5 km ASC COUNT 20 WITHDIST` |
| `trk:lastseen` | Sorted set | üye = courierId, skor = unix ms | 30 sn'den eskiler GEO sonucundan elenir; temizlik işi 5 dk'dan eskileri GEO'dan siler |
| `trk:lastseq` | Hash | courierId → son seq | Sırası bozuk noktayı tespit eder (I-8) |
| `trk:lastrec` | Hash | courierId → `recorded_at` (unix ms, **cihaz saati**) | Anlık konumla **aynı Lua script'inde** yazılır. `GetLastLocations` bunu döner. `trk:lastseen` ise sunucu saatidir: biri "ne zaman kaydedildi", öbürü "hâlâ gönderiyor mu" sorusuna cevap verir; çevrimdışı birikmiş noktalarda dakikalarca farklı olabilir |

> **Atomik yazma (F04):** Anlık konum, `lastseen`, `lastseq`, `lastrec` ve Pub/Sub yayını **tek bir Lua script'inde** yazılır: "seq mevcuttan büyük mü?" kontrolü ile yazma arasına başka bir akış giremez (I-8). Redis Cluster'da bu script'in anahtarlarının aynı slot'ta olması gerekir (tek düğümde sorun yok).

| Pub/Sub kanalı | Yayınlayan | Abone | İçerik |
|---|---|---|---|
| `trk:courier:{courierId}:location` | Tracking | Ops API | `{courierId, lat, lon, recordedAt, seq}` |

> Redis'teki Tracking verisi **türetilmiş** veridir: Redis silinirse kuryelerin sonraki konum gönderimleriyle (3 sn) yeniden dolar. Doğruluk kaynağı değildir (ADR-001).

## Dispatch Redis

| Pub/Sub kanalı | Yayınlayan | Abone | İçerik |
|---|---|---|---|
| `dsp:courier:{courierId}:offers` | Dispatch (herhangi bir instance) | Kuryenin gRPC akışını tutan Dispatch instance'ı | `{offerId}` — ayrıntı DB'den okunur |

## Notifications (`rota_notifications`)

```sql
CREATE TABLE webhook_subscriptions (
    merchant_id      text PRIMARY KEY,
    url              text        NOT NULL,      -- yalnız https (Development'ta http izinli)
    secret_protected text        NOT NULL,      -- ASP.NET Data Protection ile şifreli
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL
);

CREATE TABLE webhook_deliveries (
    id                uuid PRIMARY KEY,
    merchant_id       text        NOT NULL,
    event_id          uuid        NOT NULL,
    event_type        text        NOT NULL,     -- 'order.assigned'
    payload           jsonb       NOT NULL,
    status            text        NOT NULL,     -- Pending, Succeeded, Dead
    attempt_count     int         NOT NULL DEFAULT 0,
    next_attempt_at   timestamptz NULL,
    last_status_code  int         NULL,
    last_error        text        NULL,
    created_at        timestamptz NOT NULL,
    delivered_at      timestamptz NULL,
    UNIQUE (merchant_id, event_id)
);
CREATE INDEX ix_deliveries_due ON webhook_deliveries (next_attempt_at) WHERE status = 'Pending';
CREATE INDEX ix_deliveries_dead ON webhook_deliveries (merchant_id, created_at DESC) WHERE status = 'Dead';
```

## Ops API (`rota_ops`) — okuma modeli

```sql
CREATE TABLE order_view (
    order_id            uuid PRIMARY KEY,
    merchant_id         text        NOT NULL,
    merchant_order_ref  text        NOT NULL,
    status              text        NOT NULL,
    courier_id          uuid        NULL,
    pickup_lat double precision NOT NULL, pickup_lon double precision NOT NULL,
    dropoff_lat double precision NOT NULL, dropoff_lon double precision NOT NULL,
    created_at          timestamptz NOT NULL,
    assigned_at         timestamptz NULL,
    picked_up_at        timestamptz NULL,
    delivered_at        timestamptz NULL,
    order_version       int         NOT NULL     -- yalnız daha yeni sürümlü olay uygulanır
);
CREATE INDEX ix_order_view_status_created ON order_view (status, created_at DESC, order_id DESC);

CREATE TABLE courier_view (
    courier_id    uuid PRIMARY KEY,
    display_name  text        NOT NULL,
    state         text        NOT NULL,
    current_order uuid        NULL,
    updated_at    timestamptz NOT NULL
);
```

- **Sırası bozuk olay:** `UPDATE order_view SET ... WHERE order_id = @id AND order_version < @v`. Eski olay geç gelirse 0 satır güncellenir, yok sayılır.
- Okuma modeli tamamen olaylardan kurulduğu için silinip yeniden kurulabilir (bu ihtiyaç ölçümle doğarsa Kafka değerlendirmesi: ADR-013).
- Kişisel veri (müşteri adı/telefonu) okuma modeline **kopyalanmaz**; panel ihtiyaç duyarsa Orders'tan yetkiyle istenir.
