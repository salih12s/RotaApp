# 08 — Yol haritası

## Durum

| Faz | Ad | Boyut | Durum |
|---|---|---|---|
| F00 | İskelet, altyapı ve CI | M | ✅ PASS |
| F01 | Domain çekirdeği | M | ✅ PASS |
| F02 | Orders servisi | L | ✅ PASS |
| F03 | Güvenilir mesajlaşma (Outbox/Inbox) | L | ✅ PASS |
| F04 | Tracking: konum akışı | L | ✅ PASS |
| F05 | Dispatch: otomatik atama | XL | ✅ PASS |
| F06 | Teslimat akışı ve iptal kuralları | M | ⏭️ Sıradaki |
| F07 | Notifications: güvenilir webhook | M | Bekliyor |
| F08 | Gateway + Keycloak + yetki | L | Bekliyor |
| F09 | Ops API: GraphQL ve canlı takip | L | Bekliyor |
| F10 | Gözlemlenebilirlik | M | Bekliyor |
| F11 | Performans ölçümü ve optimizasyon | L | Bekliyor |
| F12 | Container'lar, dağıtım ve README | M | Bekliyor |
| F13 | *(opsiyonel)* Dispatcher harita arayüzü | M | Bekliyor |
| F14 | *(opsiyonel, koşullu)* Kafka ile yeniden oynatma | M | Bekliyor |

> **Devir notu (2026-11-12, F05 sonu):** F00–F05 bitti ve commit'li (`faz/f05-dispatch`, F04'ün üstünden açıldı; `main`'e henüz PR yok). Sıradaki **F06** (M, Sonnet yeter, yeni oturumda). F06 için hazır olanlar: Dispatch'te kurye `OnDelivery`'den yalnız Orders'ın `OrderStatusChanged` (`Delivered`/`Returned`/`Cancelled`) olayıyla çıkıyor (testli); `Courier.FinishDelivery` etkin. **Açık karar (Salih):** `02 §5.4` ("yeniden başlatınca yeni iş") ile `04` (`order_id` PK) çelişiyordu, F05'te aynı iş yeniden açılıyor (`Unassigned → Searching`); ayrıntı `02 §5.4` notunda. Bilinen açıklar: bayat kurye temizlik işi, konum hız sınırı, DB `/health/ready` kontrolü (F04'ten), ilk aramalarda 200 ms deadline soğuk başlangıçta aşılabiliyor, Redis kapalıyken Tracking konum alımı hata veriyor (bkz. `docs/evidence/f05/README.md`). Yerelde servis çalıştırmak için PostgreSQL port çakışması (5432) ve user-secrets anahtarları gerekir (bkz. `docs/evidence/f02/README.md`); Dispatch yerelde HTTP `5102`, gRPC `5202`. Test komutu: `dotnet test RotaApp.slnx` (Docker açık olmalı; 552 test).

Durum değerleri: `Bekliyor` · `⏭️ Sıradaki` · `🔨 Devam ediyor` · `✅ PASS` · `❌ FAIL (gerekçe)`. Faz kapanınca bu tablo aynı PR'da güncellenir.

**Sıranın mantığı:** Önce saf iş kuralları (F01), sonra siparişin kaydı (F02), sonra kayıt güvenilir şekilde dışarı nasıl çıkar (F03). Atama (F05) hem siparişe hem konuma (F04) bağlı olduğu için onlardan sonra. Auth (F08) servisler oturduktan sonra; ama yetki kuralları F02'den itibaren `DevAuth` ile yazılıyor (ADR-006). Optimizasyon ölçümden (F11) önce yapılmaz.

---

## Bir faz nasıl yürütülür? (ajan protokolü)

1. **Açıklama:** Fazın hangi problemi çözdüğünü, öğrenilecek kavramları ve riskli noktaları kısa Türkçe ile anlat. Salih onaylamadan koda başlama.
2. **Branch:** `faz/fNN-kisa-ad`.
3. **Küçük adımlar:** Her adım derlenir ve testleriyle birlikte gelir. Test türünü neden seçtiğini söyle.
4. **Doğrulama:** Fazın "Doğrulama" komutlarını çalıştır; gerçek çıktıyı göster.
5. **Rapor:** PASS kriterlerinin her biri için ✅/❌ + kanıt. Yapılmayan/doğrulanmayan her şey ayrı listede. FAIL sinyallerinden biri varsa faz PASS değildir.
6. **Kapanış:** Durum tablosu ve gerekiyorsa ADR/belgeler aynı PR'da güncellenir. Kanıtlar `docs/evidence/fNN/` altına.

---

## F00 — İskelet, altyapı ve CI

**GOAL:** Boş ama profesyonel kurulmuş bir repo: her şey derleniyor, altyapı tek komutla kalkıyor, CI her push'ta build + test koşuyor.

**Neden şimdi?** Sonraki her faz bu zemin üzerine kurulur. Paket sürümü, uyarı politikası ve klasör yapısı sonradan değiştirmesi en pahalı şeylerdir.

**Öğrenilecek kavramlar:** Central Package Management, `Directory.Build.props`, `global.json`, `.editorconfig`, compose health check'leri, GitHub Actions.

**Yapılacaklar**
- [x] `git init`, `dotnet new gitignore`, `.gitattributes` (`* text=auto`), `.editorconfig` (C# stil + isimlendirme kuralları)
- [x] Şablon projeyi (`RotaApp/RotaApp.csproj`, WeatherForecast, Controllers) solution'dan ve diskten kaldır
- [x] `global.json`: SDK `10.0.401`, `rollForward: latestFeature`
- [x] `Directory.Build.props`: `net10.0`, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`, servislerde `InvariantGlobalization=true` (Türkçe "I/ı" kültür hatalarına karşı)
- [x] `Directory.Packages.props` (Central Package Management)
- [x] [03-architecture.md §6](03-architecture.md)'daki `src/` projelerinin boş iskeletleri; `.slnx`'e solution klasörleriyle ekle
- [x] `Rota.BuildingBlocks.Web`: ortak `AddRotaServiceDefaults()` — health uçları (`/health/live`, `/health/ready`), ProblemDetails, temel OpenTelemetry (OTLP → dashboard)
- [x] Her serviste yalnızca health uçları + OpenAPI; portlar [03 §7](03-architecture.md)'deki gibi
- [x] `deploy/docker-compose.yml`: PostgreSQL (`postgres/init.sql` ile 6 veritabanı), RabbitMQ (management), Redis, Aspire Dashboard; sabit imaj etiketleri, health check, named volume
- [x] `RotaApp.slnLaunch`: VS Multiple Startup profili ("Tüm servisler")
- [x] `tests/Rota.TestKit` (boş) + `tests/Rota.Orders.UnitTests` (bir smoke testi)
- [x] `.github/workflows/ci.yml`: restore → `build -warnaserror` → `test` (trx sonuçları artifact)
- [x] [03 §8](03-architecture.md) paket tablosuna "doğrulanan sürüm", "lisans" ve "doğrulandığı faz" sütunları. F00'da kullanılan paketler ve Hot Chocolate NuGet'ten kontrol edilir; diğerleri ilk kullanıldıkları fazda.
- [x] Kısa `README.md` (proje adı, tek cümle, "yapım aşamasında", nasıl çalıştırılır)

**Kapsam dışı:** Herhangi bir iş kuralı, veritabanı tablosu, Keycloak.

**PASS kriterleri**
1. `dotnet build RotaApp.slnx -warnaserror` → 0 uyarı, 0 hata.
2. `dotnet test RotaApp.slnx` → geçiyor.
3. `docker compose -f deploy/docker-compose.yml up -d` sonrası `docker compose ps` → postgres, rabbitmq, redis `healthy`; dashboard `Up` ve UI'ı (`localhost:18888`) yanıt veriyor. (Dashboard imajı distroless; içinde health check komutu çalıştırılamaz.)
4. VS'te solution açılıyor; "Tüm servisler" profiliyle F5 → her servisin `/health/live` ucu 200.
5. F00'da kullanılan paketler ve Hot Chocolate, [03 §8](03-architecture.md) tablosunda doğrulanmış sürüm ve lisansla yazılı; diğer paketler "ilk kullanımda" olarak işaretli.

**FAIL sinyalleri:** Şablon dosyaları duruyor · bir `.csproj` içinde `Version=` var · gerekçesiz `NoWarn` · `latest` imaj etiketi.

**Doğrulama**
```bash
dotnet build RotaApp.slnx -warnaserror
```
```bash
dotnet test RotaApp.slnx
```
```bash
docker compose -f deploy/docker-compose.yml up -d && docker compose -f deploy/docker-compose.yml ps
```
```bash
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5101/health/live
```

**Kanıt:** Komut çıktıları, `docker compose ps` çıktısı.

---

## F01 — Domain çekirdeği

**GOAL:** Sipariş, kurye, teklif ve atama işinin bütün kuralları; veritabanı ve ağ olmadan, tamamı unit testli.

**Neden şimdi?** İş kuralları projenin en değerli ve en ucuz test edilen kısmı. Kural yanlışsa üstüne kurulan her şey yanlıştır.

**Öğrenilecek kavramlar:** Aggregate, value object, durum makinesi, `Result` tipi (exception'sız iş hatası), mimari test, `[Theory]` ile tablo bazlı test.

**Yapılacaklar**
- [x] `Rota.BuildingBlocks.Primitives`: `Result`, `Result<T>`, `Error` (iş kuralı içermez)
- [x] Orders `Domain/`: `Order` (geçişler: `MarkDispatching`, `Assign`, `MarkUnassigned`, `Cancel`, `PickUp`, `Deliver(code)`, `FailDelivery`, `MarkReturned`), `OrderStatus` geçiş tablosu, `GeoPoint`, `Distance` (haversine), `ServiceAreaPolicy` (≤ 15 km), `DeliveryCode` (`RandomNumberGenerator` ile üretim; HMAC-SHA256 + sunucu anahtarı ile özet; 5 yanlışta kilit)
- [x] Dispatch `Domain/`: `Courier`, `Offer`, `DispatchJob`, `CandidateRanker` (yalnız `Available`, mesafeye göre, eşitlikte en taze konum), `EtaCalculator`
- [x] Mimari test: `Domain` ad alanları `Microsoft.EntityFrameworkCore` ve `Microsoft.AspNetCore` tiplerine bağımlı olamaz (ArchUnitNET veya NetArchTest — güncellik/lisans kontrol edilerek seçilir)
- [x] Unit testler; veri kurucular (`OrderBuilder`, `CourierBuilder`) `Rota.TestKit`'te

> **F01 kararları (Salih onayı, 2026-11-11):** Kurye geçersiz geçişi → `invalid-courier-transition` (daha özel olduğu yerde `courier-busy` / `courier-not-available`); `DispatchJob` için iç kodlar `invalid-job-transition`, `offer-not-expired` (06 §5). Ret/zaman aşımında iş `Offering → Searching`, 5. denemede `Unassigned` (02 §5.4). Kilit açma deneme sayacını sıfırlar. İptal edilmiş siparişe atama domain'de hata döner; "yok say + log" kararı tüketicinin (F03/F05). Mimari test: ArchUnitNET 0.13.5. Kanıt: `docs/evidence/f01/`.

> **Not (öğretici):** 4 haneli kodun yalnızca 10.000 olasılığı var; düz hash (SHA-256) çevrimdışı kaba kuvvetle saniyede kırılır. Bu yüzden sunucu tarafı gizli anahtarlı HMAC kullanıyoruz: DB sızsa bile anahtar olmadan kod bulunamaz. Asıl koruma yine de deneme kilididir.

**Kapsam dışı:** EF Core, endpoint, mesajlaşma.

**PASS kriterleri**
1. Sipariş geçiş **matrisinin tamamı** test ediliyor: her (durum, eylem) çifti için izinli → başarı, izinsiz → `invalid-order-transition`.
2. Kurye ve teklif geçişleri aynı şekilde matrisle test ediliyor.
3. Haversine, bağımsız bir hesap aracıyla doğrulanmış en az 3 nokta çiftinde ±%0,5 içinde.
4. 5. yanlış teslim kodundan sonra doğru kod da reddediliyor; kilit açılınca kabul ediliyor.
5. Mimari test geçiyor.
6. Testlerde `DateTime.Now`/`UtcNow` yok; zaman `FakeTimeProvider` ile.

**FAIL sinyalleri:** Domain sınıfında `public set` ile durum değiştirilebiliyor · iş hatası için exception fırlatılıyor · geçiş kuralları `if` zinciri olarak birden çok yere dağılmış.

**Doğrulama**
```bash
dotnet test tests/Rota.Orders.UnitTests
```
```bash
dotnet test tests/Rota.Dispatch.UnitTests
```

---

## F02 — Orders servisi

**GOAL:** İşletme sipariş oluşturabilir, sorgulayabilir, listeleyebilir ve iptal edebilir; aynı istek iki kez gelse de tek sipariş oluşur; olay outbox'a aynı transaction'da yazılır.

**Neden şimdi?** Bütün akışın girişi. İdempotency ve outbox burada doğru kurulmazsa sonraki her servis kirli veri alır.

**Öğrenilecek kavramlar:** EF Core Fluent API, migration, `xmin` ile optimistic concurrency, idempotency anahtarı, unique index'in eşzamanlı insert'te bekletmesi, cursor pagination, ProblemDetails, .NET 10 Minimal API doğrulaması, Testcontainers, `WebApplicationFactory`, ES256 JWT + JWKS.

**Yapılacaklar**
- [x] `OrdersDbContext`, konfigürasyonlar, ilk migration ([04 Orders](04-data-model.md))
- [x] `Rota.BuildingBlocks.Messaging`: `outbox_messages` / `inbox_messages` eşlemeleri ve outbox'a yazma (relay F03'te)
- [x] `Rota.BuildingBlocks.Web`: `DevAuth` şeması (yalnız Development/test), politikalar (`MerchantOnly`, `DispatcherOnly`), hata kataloğu → ProblemDetails eşlemesi, global `IExceptionHandler`
- [x] Uçlar: `POST /orders`, `GET /orders/{id}`, `GET /orders`, `POST /orders/{id}/cancel` ([05](05-contracts.md))
- [x] İdempotency: gövde hash'i; anahtar + sipariş + outbox **tek transaction**; eşzamanlı aynı anahtar → unique ihlali → kayıtlı yanıtı oku ve döndür; farklı gövde → 409
- [x] Takip token'ı üretimi (ES256) ve `/.well-known/tracking-jwks`
- [x] `Rota.Orders.Api.http` örnek istekleri
- [x] `Rota.TestKit`: PostgreSQL container fixture'ı, `DevAuth` başlık yardımcıları

> **F02 kararları (Salih onayı, 2026-11-11):** (1) Saklanan idempotency yanıtındaki `deliveryCode` ve `trackingUrl` AES-GCM ile şifreli (AAD = sipariş id); tekrar gelen istekte çözülüp aynı gövde döner. (2) Aynı `merchantOrderRef` + farklı anahtar → 409 `duplicate-merchant-order-ref` (06 kataloğuna eklendi). (3) `Order`'a `MerchantOrderRef`, `PickupLocation`/`DropoffLocation` (`OrderLocation`: nokta + adres) ve `Customer` eklendi. (4) Gövde hash'i ham bayttan değil, doğrulanmış isteğin standart JSON'undan (boşluk/alan sırası farkı 409 üretmez). (5) Branch `faz/f01-domain` üzerinden (lokalde `main` yok). Testcontainers paylaşımı assembly fixture ile (07 §3 güncellendi). Takip token'ı süresi belgede yoktu: varsayılan 24 saat, `Orders:TrackingTokenLifetime` ile ayarlanır. İptal de `OrderStatusChanged.v1` olayını outbox'a yazar (relay F03'te). Kanıt: `docs/evidence/f02/`.

**Kapsam dışı:** Kurye uçları (F06), olay yayınlama (F03).

**PASS kriterleri**
1. **R-5:** Aynı `Idempotency-Key` ile 10 eşzamanlı istek → hepsi 201 ve aynı gövde, DB'de 1 sipariş, 1 outbox satırı.
2. Aynı anahtar + farklı gövde → 409; anahtarsız → 400; 15 km üstü → 422; geçersiz koordinat → 400 (`errors` alanı dolu).
3. Başka işletmenin siparişi → 404 (GET ve cancel).
4. Sipariş kaydından sonra yapay hata → ne sipariş ne outbox satırı kalıyor (rollback testi).
5. Cursor listeleme: sayfalama sırasında yeni sipariş eklense de tekrar/atlama yok.
6. Migration'lar boş veritabanına sıfırdan uygulanıyor; OpenAPI belgesi üretiliyor.

**FAIL sinyalleri:** "Önce SELECT, yoksa INSERT" idempotency (unique kısıt yok) · outbox `SaveChanges`'ten sonra ayrı transaction'da · offset pagination.

**Doğrulama**
```bash
dotnet test tests/Rota.Orders.IntegrationTests
```
```bash
dotnet ef migrations list --project src/Services/Orders/Rota.Orders.Api
```

---

## F03 — Güvenilir mesajlaşma (Outbox / Inbox)

**GOAL:** Olaylar RabbitMQ kesintisinde bile kaybolmadan, tekrar gelse de bir kez işlenerek servisler arasında akıyor; ilk servisler arası tur (Orders → Dispatch → Orders) çalışıyor.

**Neden şimdi?** Projedeki her asenkron akış bu temeli kullanır. Kendimiz yazdığımız için (ADR-010) en dikkatli test edilmesi gereken altyapı burası.

**Öğrenilecek kavramlar:** Transactional outbox, inbox/deduplication, publisher confirm, ACK/NACK, prefetch, quorum queue, retry kuyruğu (TTL + dead-letter), DLQ, `traceparent` taşıma, olay zarfı, contract snapshot testi.

**Yapılacaklar**
- [x] RabbitMQ topolojisi kodla tanımlanır (exchange, tüketici kuyrukları, retry ve `.dlq` kuyrukları)
- [x] Outbox relay (`BackgroundService`): `SKIP LOCKED` ile toplu alma, publisher confirm, `processed_at`, hata sayacı, 7 günlük temizlik
- [x] Tüketici altyapısı: inbox kaydı ile iş değişikliği **aynı transaction**; commit sonrası ACK; hata → retry kuyruğu → 3 denemeden sonra DLQ
- [x] `Rota.Contracts.Events`: [05 §3](05-contracts.md)'teki olay kayıtları (record'lar)
- [x] `Rota.Contracts.Tests`: her olayın JSON snapshot'ı
- [x] Dispatch: `OrderCreated` tüketicisi → `dispatch_jobs` satırı (`Searching`) + `DispatchStarted.v1`
- [x] Orders: `DispatchStarted` tüketicisi → `Created → Dispatching` + `OrderStatusChanged.v1`
- [x] Trace bağlamı: publish sırasında `traceparent` başlığı, tüketimde devam

> **F03 kararları (Salih onayı, 2026-11-11):** (1) "3 deneme" = ilk çalıştırma + **3 yeniden deneme** (5 sn / 30 sn / 2 dk, kuyruk düzeyinde TTL; mesaj başına TTL baştaki mesajın arkasında bekler). (2) Dispatch'in şemasında yalnız bu fazın tabloları var (`dispatch_jobs`, outbox, inbox); teklif ve kurye tabloları F05'te. (3) Kuyrukları tüketici tanımlar; hiçbir kuyruğa uymayan olay alternate exchange ile `q.rota.unrouted`'a park edilir (kaybolmaz ama sonradan otomatik teslim edilmez, bkz. 05 §3 "Bilinen sınır"). (4) `Messaging:Enabled` ayarı: testler ve aracılar broker'sız servis çalıştırabilsin; çalışma anında okunur. (5) Doğrulama komutundaki `--filter Category=Messaging` MTP'de `--filter-trait "Category=Messaging"` olur. Kanıt: `docs/evidence/f03/`.

**Kapsam dışı:** Atama mantığı (F05).

**PASS kriterleri**
1. **R-6:** Aynı olay iki kez teslim edilince tek yan etki.
2. **R-7:** RabbitMQ durdurulmuşken 50 sipariş oluşturuluyor; RabbitMQ başlayınca 50'si de `Dispatching` durumuna geçiyor; kayıp 0.
3. Zehirli (hep hata veren) mesaj 3 denemeden sonra DLQ'da; arkasındaki mesajlar işlenmeye devam ediyor.
4. Orders POST → relay → Dispatch tüketicisi → Orders tüketicisi zinciri dashboard'da **tek trace** olarak görünüyor.
5. Contract snapshot'ları mevcut; bir alanı silmek testi kırıyor.

**FAIL sinyalleri:** Herhangi bir yerde doğrudan `BasicPublishAsync` (outbox dışı) · ACK'in commit'ten önce yapılması · `autoAck: true`.

**Doğrulama**
```bash
dotnet test tests/Rota.Orders.IntegrationTests --filter-trait "Category=Messaging"
```
```bash
dotnet test tests/Rota.Contracts.Tests
```
```bash
docker compose -f deploy/docker-compose.yml stop rabbitmq
```
```bash
docker compose -f deploy/docker-compose.yml start rabbitmq
```

---

## F04 — Tracking: konum akışı

**GOAL:** Kuryeler gRPC akışıyla konum gönderiyor; anlık konum Redis'te, geçmiş PostgreSQL'de; "yakındaki kuryeler" sorgusu milisaniyelerde dönüyor.

**Neden şimdi?** Atama (F05) yakındaki kurye bilgisine bağlı.

**Öğrenilecek kavramlar:** gRPC client-streaming ve unary, protobuf, deadline, Redis GEO (`GEOADD`, `GEOSEARCH`), Lua script ile atomik karşılaştır-ve-yaz, `Channel<T>` ile geri basınç, Npgsql `COPY`, tablo bölümleme (partitioning), simülatör tasarımı.

**Yapılacaklar**
- [x] `Rota.Contracts.Protos`: `tracking.v1` ([05 §2](05-contracts.md)), `Grpc.Tools` ile derleme
- [x] `StreamLocations`: kurye kimliği token/`DevAuth`'tan; seq kontrolü + `GEOADD` + `lastseen` + `lastseq` **tek Lua script**'te (aynı kuryenin iki eşzamanlı akışı konumu geri alamaz)
- [x] Redis Pub/Sub yayını `trk:courier:{id}:location`
- [x] Toplu yazıcı: sınırlı `Channel<T>` → 500 ms / 1.000 nokta → `COPY` → staging → `INSERT ... ON CONFLICT DO NOTHING`
- [x] Bölüm bakım işi: önümüzdeki 3 günün bölümünü oluştur, 30 günden eskiyi `DROP`
- [x] `GetNearbyCouriers` (`GEOSEARCH` + `lastseen` tazelik filtresi), `GetLastLocations`
- [x] `Rota.Sim.CourierFleet` v1: N kurye İstanbul sınırları içinde rastgele rotada, 3 sn'de bir; parametreli kopma ve sırasız gönderim
- [x] İlk kaba ölçüm: kaç nokta/sn işlendi (hedef yok, yalnız taban çizgisi)

> **F04 kararları (Salih onayı, 2026-11-11, plan kısmı; ayrıntılar uygulama sırasında):** (1) Geçmiş yazımı ham `COPY`, EF yok; bölümlü tablo migration'da elle SQL (`TrackingDbContext` model içermez, yalnız migration taşır). (2) Redis erişimi `StackExchange.Redis` + `LuaScript`. (3) Simülatör koordinat üretir, harita çizmez. (4) Tracking olay yayınlamaz (outbox yok). **Uygulama sırasında eklenenler (planda yoktu):** `service` rolü ve `ServiceOnly` politikası (sorgu uçları yalnız servislere açık); `trk:lastrec` anahtarı (cihaz saati, `GetLastLocations` için); `recorded_at` kabul penceresi (24 sa geri, 5 dk ileri; bölümsüz satırı önler); `INVALID_ARGUMENT` akışı sonlandırır; geçmiş yazımı en iyi çabadır (DB düşerse toplu yazma düşürülür, akış durmaz); `GetNearbyCouriers` aramayı tazelik filtresiyle tek script'te genişleterek yapar. Kanıt: `docs/evidence/f04/`, ölçüm: `perf/reports/2026-11-11-tracking-baseline.md`.

**Kapsam dışı:** Teklif akışı (F05), Ops API yayını tüketimi (F09).

**PASS kriterleri**
1. **R-8:** Sırasız gelen nokta anlık konumu geri almıyor ama geçmişe yazılıyor.
2. Aynı nokta iki kez gelirse DB'de tek satır.
3. Bilinen mesafelerde konumlandırılmış fixture kuryeleriyle `GetNearbyCouriers` doğru kümeyi doğru sırada döndürüyor; 30 sn'den eski konumlu kurye sonuçta yok.
4. Aynı kurye için iki eşzamanlı akış konumu geri almıyor.
5. Bölüm işi `FakeTimeProvider` ile: yeni bölümler oluşuyor, 30 günden eskiler siliniyor.
6. Taban çizgisi ölçümü `perf/reports/` altında.

**FAIL sinyalleri:** Sınırsız kuyruk/liste ile tamponlama · sıcak yolda nokta başına EF `SaveChanges` · kurye kimliğinin mesajdan okunması.

**Doğrulama**
```bash
dotnet test tests/Rota.Tracking.IntegrationTests
```
```bash
dotnet run --project src/Simulators/Rota.Sim.CourierFleet -- --couriers 200 --interval 3
```

---

## F05 — Dispatch: otomatik atama

**GOAL:** Yeni sipariş en yakın uygun kuryeye teklif ediliyor; kabul/ret/zaman aşımı doğru işleniyor; **yük altında bile çift atama yok**.

**Neden şimdi?** Projenin kalbi; F02–F04'ün hepsine dayanıyor.

**Öğrenilecek kavramlar:** Koşullu update ile rezervasyon, kısmi unique indeks, `FOR UPDATE SKIP LOCKED`, gRPC client (`Grpc.Net.ClientFactory`) + deadline, gRPC server-streaming, Redis Pub/Sub ile instance'lar arası yönlendirme, yeniden bağlanma, nihai tutarlılık.

**Yapılacaklar**
- [x] Tablolar ve migration ([04 Dispatch](04-data-model.md)); vardiya uçları
- [x] Aday arama: Tracking `GetNearbyCouriers` (deadline 200 ms) → `CandidateRanker`
- [x] Teklif verme: kurye koşullu update + teklif insert + outbox (`CourierStateChanged`) tek transaction; ardından Redis'e yayın
- [x] `SubscribeOffers` server-streaming: bağlanınca bekleyen teklif, Redis kanalına abonelik, 15 sn heartbeat, iptal/zaman aşımında `OfferWithdrawn`
- [x] Kabul/ret uçları (koşullu update) → `CourierAssigned.v1`
- [x] Zaman aşımı işi (`SKIP LOCKED`), sıradaki aday, en fazla 5 deneme → `DispatchFailed.v1`; aday yoksa `next_search_at` ile yeniden arama (3 dk)
- [x] Elle atama ve yeniden başlatma uçları
- [x] `OrderStatusChanged` tüketicisi: `Cancelled` → teklif/iş iptal, kurye serbest; `Delivered`/`Returned` → kurye `Available`
- [x] Orders: `CourierAssigned` ve `DispatchFailed` tüketicileri
- [x] `CourierFleetSim` v2: teklifleri olasılıkla kabul/ret eder, bazen hiç cevap vermez
- [x] Metrik: sipariş → ilk teklif süresi

> **F05 kararları (Salih onayı: faz onayı 2026-11-12; aşağıdakiler uygulama sırasında alındı, plan kısmı yoktu):** (1) Her durum geçişi **koşullu UPDATE**'tir; kurye geçişleri doğrudan SQL, iş geçişinde durumu **domain** seçer, yazma `status` + `offer_attempts` koşuluyla yapılır. Kilit sırası teklif → iş → kurye (04). (2) Zaman aşımı ve iş arama `FOR UPDATE SKIP LOCKED`; arama işçisi işi **kira** ile alır (`next_search_at` ileri itilir). (3) Redis yalnız uyandırma: yayın commit'ten sonra (`DbContext.AfterCommit`) ve `FireAndForget`; akış DB'den okur, Redis yokken 1 sn'lik yoklamaya düşer. (4) Bir kurye bir siparişin teklifini bir kez alır. (5) Tracking yanıt vermezse iş ertelenir (`Postpone`), deneme sayısı artmaz. (6) Kurye ilk `shift/start`'ta kendini kaydeder (F08'e kadar). (7) `DispatchJob`'a `Postpone`/`Restart`/`ManualAssign`, `Courier`'a `Version`; `dispatch_jobs`'a adres/varış, `couriers`'a `version` sütunları eklendi (04). (8) **Çelişki, karar bekliyor:** yeniden başlatma aynı işi açar (`02 §5.4` notu). Kanıt: `docs/evidence/f05/`.

**Kapsam dışı:** Teslim alma/teslim (F06), gerçek kimlik (F08).

**PASS kriterleri**
1. **R-1:** 10 kurye + 100 eşzamanlı sipariş, 20 tur → hiçbir kuryenin birden fazla aktif teklifi/siparişi yok, hiçbir siparişin birden fazla kabul edilmiş teklifi yok (SQL ile doğrulanır).
2. **R-2:** Kabul ve zaman aşımı aynı anda → yalnızca biri uygulanıyor (20 tur).
3. **R-3:** İki zaman aşımı işçisi aynı anda çalışırken her teklif bir kez işleniyor.
4. Teklif kuryenin akışına ≤ 1 sn içinde ulaşıyor; akış kopukken oluşan teklif yeniden bağlanınca geliyor.
5. Tracking yavaşken (yapay 1 sn gecikme) deadline devreye giriyor, iş `Searching`'de kalıp tekrar deneniyor.
6. Redis container'ı durdurulunca atama erteleniyor, **çift atama oluşmuyor**; Redis gelince atama devam ediyor.
7. Elle atamada müsait olmayan kurye → 409.

**FAIL sinyalleri:** Kurye durumunun Redis'te tutulması · "oku–kontrol et–yaz" ile rezervasyon · teklif süresinin `Task.Delay` ile beklenmesi · testlerin gerçek 30 sn beklemesi.

**Doğrulama**
```bash
dotnet test tests/Rota.Dispatch.IntegrationTests
```
```bash
dotnet test tests/Rota.Dispatch.IntegrationTests --filter Category=Race
```

---

## F06 — Teslimat akışı ve iptal kuralları

**GOAL:** Atanan kurye paketi teslim alıp kodla teslim edebiliyor; iptal kuralları ve yarışları doğru çalışıyor; ilk uçtan uca senaryo baştan sona koşuyor.

**Öğrenilecek kavramlar:** Kaynak bazlı yetkilendirme (atanmış kurye), optimistic concurrency yarışı, deneme kilidi, nihai tutarlılık senaryoları.

**Yapılacaklar**
- [ ] Orders kurye uçları: `pickup`, `deliver`, `delivery-failed`, `returned`; dispatcher `delivery-code/unlock`
- [ ] Atanmış kurye kontrolü (`assigned_courier_id`, `CourierAssigned` olayından)
- [ ] Atanmış siparişin iptali → Dispatch kuryeyi serbest bırakıyor
- [ ] `CourierFleetSim` v3: kabul ettiği siparişe gider, teslim alır, kodu girer (bazen yanlış)
- [ ] `MerchantSim` v1: sipariş üretir, teslim kodunu simülatöre iletir
- [ ] Uçtan uca manuel senaryo: 50 sipariş, simülatörlerle

**PASS kriterleri**
1. **R-4:** İptal ve teslim alma aynı anda → biri 200, diğeri 409 (20 tur).
2. 5 yanlış kod → kilit; kilitliyken doğru kod 409; unlock sonrası doğru kod 200.
3. Atanmamış kurye `pickup`/`deliver` → 404.
4. Teslimden sonra Dispatch'te kurye `Available` (≤ 2 sn).
5. Simülatörlerle 50 siparişin tamamı terminal duruma (`Delivered`/`Returned`/`Cancelled`) ulaşıyor; takılı sipariş yok (SQL ile doğrulanır); trace ekran görüntüsü.

**Doğrulama**
```bash
dotnet test tests/Rota.Orders.IntegrationTests
```
```bash
dotnet run --project src/Simulators/Rota.Sim.Merchant -- --orders 50
```

---

## F07 — Notifications: güvenilir webhook

**GOAL:** Her sipariş durumu değişikliği işletmeye imzalı webhook ile en az bir kez ulaşıyor; işletme kapalıyken birikenler açılınca gidiyor; yavaş bir işletme diğerlerini etkilemiyor.

**Öğrenilecek kavramlar:** HMAC imza, replay koruması, üstel bekleme (exponential backoff), DLQ ve yeniden oynatma, circuit breaker, bulkhead, SSRF, ASP.NET Data Protection (anahtar kalıcılığı), `HttpClientFactory` + typed client + timeout.

**Yapılacaklar**
- [ ] Abonelik uçları; secret üretimi; Data Protection anahtarlarının **kalıcı** saklanması (yeniden başlatmada secret'lar çözülebilmeli)
- [ ] URL doğrulama: https + DNS çözümlemesi sonrası özel IP reddi
- [x] `OrderStatusChanged` tüketicisi → `webhook_deliveries`
- [ ] Teslim işçisi: `SKIP LOCKED`, imza, 10 sn timeout, backoff tablosu, `Dead`; işletme başına bulkhead (eşzamanlı 4) ve circuit breaker
- [ ] DLQ listeleme ve replay uçları
- [ ] `MerchantSim` webhook alıcısı: imza doğrulama, hata modları (500, timeout, yavaş)
- [ ] README için örnek "webhook doğrulama" kodu

**PASS kriterleri**
1. **R-9:** Alıcı 5 dk kapalı → açılınca bütün olaylar teslim; tekrarlar `id` ile ayıklanabiliyor.
2. MerchantSim imzayı doğruluyor; değiştirilmiş gövde ve 5 dk'dan eski `t` reddediliyor.
3. A işletmesi 10 sn geciktirirken B işletmesinin teslim p95'i < 2 sn.
4. `https://127.0.0.1`, `http://10.0.0.5` gibi URL'ler 400.
5. Deneme limiti aşılınca `Dead`; replay sonrası teslim.
6. Servis yeniden başlatıldıktan sonra mevcut secret'larla imzalama çalışıyor.

**Doğrulama**
```bash
dotnet test tests/Rota.Notifications.IntegrationTests
```

---

## F08 — Gateway + Keycloak + yetki

**GOAL:** Bütün dış trafik gateway'den geçiyor; gerçek token'larla her rol yalnızca kendine izin verileni yapabiliyor; rate limit iki gateway instance'ında toplam uygulanıyor.

**Öğrenilecek kavramlar:** OIDC, client credentials, JWT doğrulama (authority, audience, imza anahtarı rotasyonu), YARP route/cluster, gRPC ve WebSocket proxy, dağıtık rate limiting, savunma derinliği.

**Yapılacaklar**
- [ ] Keycloak compose'a; `deploy/keycloak/realm-rota.json`: roller, merchant client'ları (`merchant_id` claim eşleyicisi), test kurye/dispatcher kullanıcıları (yalnız seed dosyasında)
- [ ] Bütün servislerde `JwtBearer`; `DevAuth` Development dışında kayıtlıysa başlangıç hatası
- [ ] YARP: REST, gRPC (HTTP/2) ve WebSocket rotaları; `traceparent` geçişi
- [ ] Redis tabanlı dağıtık rate limit ([06 §3](06-security-and-errors.md))
- [ ] Simülatörler token alıyor (dev realm'de test kullanıcıları için direct access grant; gerçek mobilde Authorization Code + PKCE olacağı README'de belirtilir)
- [ ] `.http` dosyalarına token alma adımı
- [ ] Testler: `Testcontainers.Keycloak` ile gerçek token

**PASS kriterleri**
1. **R-10:** Rol × uç matrisi testli: token yok 401, yanlış rol 403, başkasının kaynağı 404.
2. Gateway'i atlayıp doğrudan servise token'sız istek → 401 (savunma derinliği).
3. İki gateway instance'ına bölünmüş, sınırın 2 katı istek → yaklaşık sınır kadarı geçiyor.
4. `DevAuth` Production yapılandırmasında uygulama başlamıyor (test).

**Doğrulama**
```bash
dotnet test RotaApp.slnx --filter Category=Auth
```

---

## F09 — Ops API: GraphQL ve canlı takip

**GOAL:** Dispatcher bütün operasyonu tek GraphQL uç noktasından sorgulayıp canlı izliyor; müşteri imzalı linkle yalnızca kendi teslimatını görüyor.

**Öğrenilecek kavramlar:** Hot Chocolate tip sistemi, okuma modeli (CQRS), filtering/sorting/cursor pagination, projection, DataLoader ve N+1, mutation conventions ve domain hataları, subscription (Redis sağlayıcı), query cost analysis, GraphQL yetkilendirme.

**Yapılacaklar**
- [ ] Hot Chocolate sürümü (F00 tablosundan) ve kurulum
- [ ] Okuma modeli tüketicileri (`OrderCreated`, `OrderStatusChanged`, `CourierStateChanged`); sürüm koruması (`order_version <`)
- [ ] Sorgular: `orders` (filtre, sıralama, cursor), `order`, `couriers`, `dispatchOverview`
- [ ] DataLoader'lar: `courier_view` toplu okuma, Tracking `GetLastLocations` toplu gRPC
- [ ] Mutation'lar: `assignCourier`, `restartDispatch` → Dispatch typed client (timeout, circuit breaker, retry yok); hata tipleri
- [ ] Subscription'lar: Redis sağlayıcı; Tracking kanallarına desen aboneliği; `orderTracking` için JWKS ile token doğrulama ve durum kontrolü
- [ ] Maliyet ve derinlik sınırları; `dispatcher` rol yetkisi

**PASS kriterleri**
1. **R-12:** 50 siparişlik `orders { courier { lastLocation } }` sorgusu → en fazla 2 DB sorgusu + 1 gRPC çağrısı (sayaçla doğrulanır).
2. **R-11:** Süresi dolmuş/başka siparişe ait takip token'ı reddediliyor; `Delivered` sonrası konum akışı kesiliyor.
3. Eski sürümlü olay okuma modelini geri almıyor.
4. Maliyet sınırını aşan sorgu çalıştırılmadan reddediliyor.
5. İki Ops API instance'ı: B'ye bağlı subscription, A'nın tükettiği `orderStatusChanged`'i alıyor.

**Doğrulama**
```bash
dotnet test tests/Rota.OpsApi.IntegrationTests
```

---

## F10 — Gözlemlenebilirlik

**GOAL:** Tek bir siparişin bütün yolculuğu tek trace'te görünüyor; sistemin sağlığı ve iş metrikleri ölçülüyor; loglarda kişisel veri yok.

**Öğrenilecek kavramlar:** OpenTelemetry (traces, metrics, logs), span link'leri (mesajlaşmada), histogram, structured logging scope'ları, liveness ile readiness farkı.

**Yapılacaklar**
- [ ] Trace: HTTP, gRPC, Npgsql, Redis, RabbitMQ (publish/consume span'leri elle, `traceparent` ile)
- [ ] İş metrikleri: `rota_orders_created_total`, `rota_time_to_assign_seconds` (histogram), `rota_offer_outcomes_total{outcome}`, `rota_location_points_total{result}`, `rota_outbox_lag_seconds`, `rota_webhook_attempts_total{result}`, `rota_dlq_messages`
- [ ] Log scope'ları (`orderId`, `courierId`, `merchantId`); kişisel veri maskeleme
- [ ] Health: `live` (süreç ayakta) / `ready` (DB, Redis, RabbitMQ erişilebilir)
- [ ] Kanıt ekran görüntüleri `docs/evidence/f10/`

**PASS kriterleri**
1. Sipariş → atama → teslim → webhook zinciri tek trace (span link'leriyle) olarak görünüyor.
2. PostgreSQL durdurulunca `ready` başarısız, `live` başarılı; süreç çökmüyor.
3. Metrikler dashboard'da görünüyor.
4. Bir siparişin bütün loglarında telefon/isim düz metin yok (log örneği ile).

---

## F11 — Performans ölçümü ve optimizasyon

**GOAL:** [07 §5](07-testing-and-performance.md)'teki hedefler ölçülmüş; en az bir mühendislik kararı önce/sonra sayılarıyla verilmiş.

**Öğrenilecek kavramlar:** p50/p95/p99, throughput, load/stress/spike/soak farkı, darboğaz analizi, ölçmeden optimize etmeme.

**Yapılacaklar**
- [ ] `perf/k6/`: sipariş yükü ve spike senaryoları
- [ ] `CourierFleetSim` yük modu (kurye sayısı, frekans)
- [ ] Load, stress, spike, soak (2 saat) koşuları ve raporları
- [ ] Deneyler: EF `AddRange` vs `COPY`; toplu yazma boyutu; zaman aşımı tarama aralığı
- [ ] Bulunan ilk darboğazın düzeltilmesi ve yeniden ölçüm

**PASS kriterleri**
1. Dört senaryo türünün raporu şablona uygun şekilde `perf/reports/` altında.
2. Hedefler tutuyor; tutmayanlar için gerekçe + bilinçli güncellenmiş hedef yazılı.
3. En az bir optimizasyonun önce/sonra sayıları var.
4. Load senaryosu sonrası SQL kontrolü: çift atama 0, takılı sipariş 0.

**Doğrulama**
```bash
k6 run perf/k6/orders-load.js
```

---

## F12 — Container'lar, dağıtım ve README

**GOAL:** Temiz bir makinede tek komutla bütün sistem çalışıyor; image'lar GHCR'da; README bir mülakatçıya projeyi 3 dakikada anlatıyor.

**Öğrenilecek kavramlar:** Multi-stage Dockerfile, root olmayan kullanıcı, image etiketleme, GHCR, CI'da E2E test, sürümleme.

**Yapılacaklar**
- [ ] Servis başına multi-stage Dockerfile (SDK ile build, `aspnet:10.0` runtime, root olmayan kullanıcı)
- [ ] `deploy/docker-compose.services.yml` (servisler + simülatörler)
- [ ] CI: `main`'e merge'de image build + GHCR push (`sha` ve `latest` etiketleri)
- [ ] `Rota.E2ETests`: compose üzerinde kritik akış; CI'da koşar
- [ ] README (İngilizce + Türkçe özet): [10-portfolio-evidence.md](10-portfolio-evidence.md)'deki kontrol listesi
- [ ] (K5'e göre) VPS'e dağıtım adımları

**PASS kriterleri**
1. Temiz klon → `docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.services.yml up -d` → 3 dk içinde hepsi `healthy`.
2. E2E testi CI'da geçiyor.
3. Image'lar GHCR'da.
4. README kontrol listesi tamam.

---

## F13 — *(opsiyonel)* Dispatcher harita arayüzü

**GOAL:** README'de kuryelerin canlı hareket ettiği, siparişlerin atandığı bir GIF.

- React + Vite; klasör yapısı global kurallara uygun (Bulletproof React: `app/` routing, `features/`, `components/ui/`, `lib/`).
- Harita: Leaflet + OpenStreetMap; veri: Ops API GraphQL subscription.
- Tasarım: tek kolon, ürünün kendi diline ait dokunuşlar (rota çizgisi, kurye işaretçisi), açık tema ana tema.
- GIF: `readme-media` skill'i ile kayıt.

## F14 — *(opsiyonel, koşullu)* Kafka ile yeniden oynatma

**Koşul:** F11 sonrasında okuma modelini veya ETA kalibrasyonunu geçmiş olaylardan yeniden kurma ihtiyacı somut olarak ortaya çıkarsa. Çıkmazsa yapılmaz ve README'de "neden Kafka yok" olarak anlatılır (ADR-013).

---

## Riskler ve önlemler

| Risk | Önlem |
|---|---|
| Harita/ETA'ya dalmak | ADR-008; gerçek yol rotası kapsam dışı |
| Simülatörlerin ürün gibi büyümesi | Simülatör yalnızca o fazın test etmesi gereken davranışı üretir |
| Gerçek zamanlı testlerin flaky olması | `FakeTimeProvider`, koşul bekleme, deterministik simülatör tohumu (seed) |
| F05'in büyüklüğü | F05 kendi içinde 3 PR'a bölünebilir: (a) aday + teklif + kabul, (b) zaman aşımı + yeniden arama, (c) akış + elle atama |
| AI ajanının "çalışıyor" demesi | Faz yalnızca doğrulama çıktısıyla kapanır; FAIL sinyalleri kontrol listesi |
| Paket sürüm uyumsuzluğu (.NET 10) | [03 §8](03-architecture.md) doğrulama tablosu; paket ilk kullanıldığı fazda lisans + uyumluluk kontrolünden geçer |
