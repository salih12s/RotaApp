# 06 — Güvenlik ve hata yönetimi

## 1. Kimlik doğrulama: dört aktör, üç yöntem

| Aktör | Yöntem | Token içeriği (claim) | Neden bu yöntem? |
|---|---|---|---|
| İşletme sistemi | OAuth2 **client credentials** (Keycloak client) | `merchant_id`, rol `merchant` | Makineden makineye; kullanıcı yok |
| Kurye | OIDC kullanıcı girişi (Keycloak) | `sub` = courierId, rol `courier` | Kişiye bağlı, kısa ömürlü token |
| Dispatcher | OIDC kullanıcı girişi (Keycloak) | `sub`, rol `dispatcher`, `merchant_id` (opsiyonel kapsam) | Panel kullanıcısı |
| Son müşteri | **Takip token'ı** (Orders imzalar) | `order_id`, `exp` | Hesap açtırmadan, tek sipariş ve süreyle sınırlı erişim |

- **Takip token'ı:** Orders, kendi özel anahtarıyla (ES256) imzalı bir JWT üretir. Ops API, Orders'ın `/.well-known/tracking-jwks` ucundan **açık anahtarı** alıp doğrular. Ortak sır (shared secret) yok: Ops API token üretemez, yalnızca doğrular.
- **Savunma derinliği (defense in depth):** Gateway token'ı doğrular **ve** her servis de JWT'yi kendisi doğrular. Gateway atlatılsa (iç ağa sızma, yanlış yapılandırma) bile servis yetkisiz isteği reddeder.
- **Servisler arası iç çağrılar** (Dispatch → Tracking gRPC, Ops API → Dispatch HTTP): MVP'de iç ağ izolasyonu (compose ağı, dışarı port açılmaz) + çağıranın kendi client credentials token'ı. Bu bilinçli bir sınırdır; mTLS kapsam dışıdır ve README'de belirtilir.

### F01–F07 arası geçici kimlik doğrulama

Keycloak F08'de gelir. Ama yetki kuralları (kaynak sahipliği) ilk günden yazılmalı ve test edilmeli. Bu yüzden (ADR-006):
- Uçlar F02'den itibaren gerçek politikalarla korunur (`RequireRole`, sahiplik kontrolü).
- **Yalnızca `Development` ortamında ve testlerde** bir `DevAuth` şeması claim'leri istek başlığından okur (`X-Dev-User: courier:<guid>`).
- F08'de `DevAuth` yalnız test projesinde kalır; uygulamada `Development` dışında kaydedilirse uygulama **başlamaz** (başlangıç kontrolü).

## 2. Yetki politikaları

| Politika | Kural | Hata |
|---|---|---|
| `MerchantOnly` | rol `merchant` | 403 |
| `CourierOnly` | rol `courier` | 403 |
| `DispatcherOnly` | rol `dispatcher` | 403 |
| Sipariş sahipliği | `order.merchant_id == token.merchant_id` (merchant) | **404** |
| Atanmış kurye | `order.assigned_courier_id == token.sub` | **404** |
| Teklif sahipliği | `offer.courier_id == token.sub` | **404** |
| Takip | token imzası + `exp` + `order_id` eşleşmesi + sipariş `Assigned`/`PickedUp` | Subscription hata ile kapanır |

> Sahiplik ihlalinde 404: "Bu sipariş var ama senin değil" bilgisini sızdırmamak için (enumeration saldırısını zorlaştırır). Rol eksikliğinde 403: kim olduğunu biliyoruz, bu işlem rolüne kapalı.

## 3. Girdi doğrulama ve kötüye kullanım

| Konu | Kural |
|---|---|
| Koordinat | `lat ∈ [-90, 90]`, `lon ∈ [-180, 180]`; NaN/Infinity reddedilir |
| Mesafe | Alış–teslim kuş uçuşu > 15 km → 422 `out-of-service-area` |
| Metin alanları | Adres ≤ 300, isim ≤ 100, `merchantOrderRef` ≤ 64 karakter |
| Telefon | E.164 biçimi |
| `Idempotency-Key` | Zorunlu, 1–128 karakter |
| Teslim kodu | Tam 4 rakam; 5 yanlış → kilit; dispatcher açar |
| Webhook URL | Yalnız `https` (Development hariç); **özel/iç IP adreslerine izin yok** (SSRF koruması: `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `127.0.0.0/8`, `169.254.0.0/16`, `::1`) — DNS çözümlemesinden sonra kontrol |
| gRPC konum akışı | Mesaj başına boyut sınırı; saniyede kurye başına en fazla 5 nokta (fazlası düşürülür, metrik artar) |

### Rate limit (Gateway)

| Kapsam | Sınır (başlangıç) | Anahtar |
|---|---|---|
| Sipariş oluşturma | 50 istek/sn | `merchant_id` |
| Diğer REST | 200 istek/sn | `sub` / `merchant_id` |
| Anonim takip bağlantısı | 10 bağlantı/dk | IP |

Gateway 2 instance çalıştığında sınırın instance başına değil **toplam** uygulanması için sayaçlar Redis'te tutulur (F08). Tek instance'ta ASP.NET Core'un yerleşik rate limiter'ı yeterlidir; Redis'in gerekçesi ikinci instance'tır.

## 4. Kişisel veri (KVKK) ve sırlar

- Kişisel veri: müşteri adı, telefonu, adresi; kurye konumu.
- Loglarda telefon maskelenir (`+90555***2233`); adres ve isim loglanmaz. Yapılandırılmış log alanlarında kişisel veri için ayrı bir maskeleme yardımcısı kullanılır.
- Kurye konum geçmişi 30 gün sonra bölüm (partition) silinerek yok edilir.
- Müşteri, kuryenin konumunu yalnızca aktif teslimat süresince görür.
- Sırlar (`ConnectionStrings`, webhook secret'ları, Keycloak client secret'ları) repoda yok: lokal `dotnet user-secrets`, container'da ortam değişkeni. Webhook secret'ları DB'de ASP.NET Data Protection ile şifreli.
- Test kullanıcıları ve şifreleri yalnızca `deploy/keycloak/realm-rota.json` seed dosyasında; gerçek ortamda kullanılmaz.

## 5. Hata modeli

- **Beklenen iş hatası** (geçersiz geçiş, süresi dolmuş teklif, yanlış kod): domain metodu sonuç tipi döner (`Result<T>` / hata nesnesi), exception fırlatmaz. Endpoint bunu ProblemDetails'e çevirir.
- **Beklenmeyen hata:** global `IExceptionHandler` yakalar → 500 ProblemDetails (`type: unexpected-error`, `traceId`), stack trace yalnızca loga.
- **Eşzamanlılık çakışması:** `DbUpdateConcurrencyException` → 409 `concurrency-conflict`; istemci yeniden okuyup tekrar deneyebilir.

### Hata kataloğu

`type` alanı: `https://rota.dev/errors/<kimlik>`.

| Kimlik | HTTP | Ne zaman? |
|---|---|---|
| `validation-failed` | 400 | Alan doğrulaması (`errors` sözlüğüyle) |
| `idempotency-key-missing` | 400 | `POST /orders` başlıksız |
| `invalid-delivery-code` | 400 | Yanlış kod; `remainingAttempts` eklenir |
| `unauthorized` | 401 | Token yok/geçersiz |
| `forbidden` | 403 | Rol eksik |
| `not-found` | 404 | Kaynak yok **veya** çağırana ait değil |
| `invalid-order-transition` | 409 | Durum makinesi geçişe izin vermiyor; `currentStatus` eklenir |
| `invalid-courier-transition` | 409 | Kurye durum makinesi geçişe izin vermiyor (ör. çevrimdışıyken vardiya kapatma) veya teklif/sipariş kuryenin mevcut işi değil; `currentState` eklenir. `courier-busy` ve `courier-not-available` daha özel olduğu için önce onlar kullanılır |
| `idempotency-key-reused` | 409 | Aynı anahtar, farklı gövde |
| `duplicate-merchant-order-ref` | 409 | Aynı işletmede aynı `merchantOrderRef` başka bir `Idempotency-Key` ile tekrar gönderildi |
| `concurrency-conflict` | 409 | Aynı kayda eşzamanlı değişiklik |
| `offer-not-pending` | 409 | Teklif süresi dolmuş / reddedilmiş / iptal |
| `courier-busy` | 409 | Teslimattayken vardiya kapatma |
| `courier-not-available` | 409 | Elle atamada kurye müsait değil |
| `job-not-assignable` | 409 | Sipariş atanabilir durumda değil |
| `invalid-job-transition` | — (iç) | `DispatchJob` durum makinesi eylemi reddetti. Dispatch'in kendi iş akışından doğar; HTTP'ye çıkarsa 500 sayılır (bug) |
| `offer-not-expired` | — (iç) | Zaman aşımı işi süresi dolmamış teklifi kapatmaya çalıştı. Sorgu koşulu (`expires_at <= @now`) bunu dışladığı için normalde oluşmaz |
| `delivery-code-locked` | 409 | 5 yanlış deneme sonrası |
| `out-of-service-area` | 422 | Mesafe sınırı aşıldı |
| `rate-limited` | 429 | `Retry-After` başlığıyla |
| `dependency-unavailable` | 503 | Zorunlu bağımlılık (DB) erişilemez |
| `unexpected-error` | 500 | Beklenmeyen hata |

## 6. Dayanıklılık (resilience) politikaları

**Kural:** Yalnızca **idempotent** işlemler otomatik retry edilir. Retry edilen her çağrının timeout'u vardır. Timeout'suz dış çağrı yasak.

| Çağrı | Timeout | Retry | Circuit breaker | Not |
|---|---|---|---|---|
| Dispatch → Tracking `GetNearbyCouriers` | 200 ms deadline | 1 kez (`UNAVAILABLE`) | — | Başarısızsa iş `next_search_at` ile ertelenir |
| Ops API → Dispatch (elle atama) | 2 sn | **yok** (komut; tekrar riskli) | Var | Kullanıcıya hata döner, kendisi tekrar dener |
| Ops API → Tracking `GetLastLocations` | 300 ms | yok | Var | Konumsuz yanıt döner (degrade) |
| Notifications → işletme webhook | 10 sn | Kendi zamanlaması (DB'de `next_attempt_at`) | İşletme başına | 5 ardışık hata → 1 dk boyunca o işletmeye istek yok |
| Outbox relay → RabbitMQ | 5 sn (confirm) | Bir sonraki turda tekrar | — | Mesaj DB'de kalır; kayıp yok |
| Tüketici iş hatası | — | 3 kez, artan gecikme | — | Sonra `.dlq` |

**Bulkhead:** Webhook teslim işçisi işletme başına eşzamanlı istek sınırı uygular (ör. 4). Yavaş bir işletme, diğer işletmelerin bildirimlerini bekletmez.

## 7. Arıza senaryoları

Her satır en az bir otomatik veya belgelenmiş manuel testle doğrulanır.

| Arıza | Beklenen davranış | Kanıt |
|---|---|---|
| RabbitMQ 2 dk kapalı | Sipariş alma devam eder (outbox birikir); açılınca bütün olaylar sırayla gider; kayıp 0 | F03 kaos testi |
| Bir tüketici aynı mesajı iki kez alır | İkinci işlemede yan etki yok (inbox) | F03 integration testi |
| Redis kapalı | Konum alımı geçmişe yazmaya devam eder; yakındaki kurye sorgusu `UNAVAILABLE` → atama ertelenir; Redis gelince 3 sn içinde indeks dolar; **çift atama yok** (doğruluk PostgreSQL'de) | F05 testi |
| Tracking kapalı/yavaş | Deadline 200 ms; sipariş `Searching`'de kalır, `next_search_at` ile tekrar denenir | F05 testi |
| Dispatch instance'ı teklif sürerken ölür | Teklif DB'de; zaman aşımı işini diğer instance `SKIP LOCKED` ile işler | F05 iki instance testi |
| Kuryenin gRPC akışı kopar | Teklif DB'de bekler; yeniden bağlanınca gönderilir; süresi dolarsa sıradaki adaya geçilir | F05 testi |
| İşletme webhook ucu 5 dk kapalı | Retry; açılınca teslim; limit aşılırsa DLQ + yeniden gönderim | F07 testi |
| PostgreSQL kapalı | `/health/ready` başarısız; yazma uçları 503; süreç çökmez | F10 health testi |
| Aynı anda iptal + teslim alma | Biri başarılı, diğeri 409 | F06 race testi |
| Kabul ile zaman aşımı aynı anda | Yalnızca biri uygulanır | F05 race testi |
