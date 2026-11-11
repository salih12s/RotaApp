# 07 — Test ve performans planı

## 1. Test türleri ve neden

| Test türü | Rota'da neyi test eder? | Neden bu tür? |
|---|---|---|
| **Unit** | Durum makineleri, haversine/ETA, aday sıralama, teslim kodu kilidi, retry gecikme hesabı, webhook imzası | Saf iş kuralları; DB/ağ yok, milisaniyede çalışır. Piramidin tabanı, en çok bunlardan yazılır. |
| **Integration** (Testcontainers) | Koşullu update ile çift atama engeli, `SKIP LOCKED`, kısmi unique indeksler, idempotency PK'si, outbox'ın aynı transaction'da yazılması, inbox | **Race condition ve transaction davranışı mock ile test edilemez.** Gerçek PostgreSQL/Redis/RabbitMQ container'ı gerekir. |
| **API** (`WebApplicationFactory`) | Status kodları (400/401/403/404/409/422/429), ProblemDetails şeması, yetki ve sahiplik | Kullanıcının gördüğü sözleşme. Her negatif durum ayrı test. |
| **gRPC** | Akışın sırasız/tekrarlı noktaları, deadline, durum kodları | Akış davranışı yalnızca gerçek gRPC kanalıyla görülür (test sunucusu üzerinden). |
| **Fake** | `CourierFleetSim`, `MerchantSim` (webhook alıcısı) | Hata modlarını (kopma, ret, 500 dönen alıcı) istediğimiz an üretmek için basit ama çalışan sahteler. |
| **Contract** | Olay JSON şemaları (`OrderCreated.v1` …) ve proto'lar | Servisler ayrı deploy edilince kırılmanın ilk yeri. Şema snapshot'ı ile hafif tutulur; snapshot değişirse PR'da bilinçli onay gerekir. |
| **E2E** | 1–2 akış: sipariş → atama → teslim alma → teslim → webhook | Yavaş ve kırılgan; yalnız kritik akış. |
| **Load / Stress / Spike / Soak** | Konum alımı, sipariş oluşturma, atama gecikmesi | Kararlar (toplu yazma boyutu, `COPY`) ölçümle verilir. |

Test piramidi hedefi (yaklaşık): unit %60 · integration/API %35 · E2E %5.

## 2. Test projeleri

| Proje | İçerik | Bağımlılık |
|---|---|---|
| `Rota.Orders.UnitTests` | `Order` durum makinesi, `DeliveryCode`, mesafe kuralı | Yok |
| `Rota.Orders.IntegrationTests` | API, idempotency, optimistic concurrency, outbox, tüketiciler | PostgreSQL container |
| `Rota.Dispatch.UnitTests` | Aday sıralama, ETA, `Courier`/`Offer` geçişleri | Yok |
| `Rota.Dispatch.IntegrationTests` | Çift atama yarışı, zaman aşımı + `SKIP LOCKED`, teklif akışı, elle atama | PostgreSQL + Redis (+ Tracking için fake gRPC sunucusu) |
| `Rota.Tracking.IntegrationTests` | Konum akışı, sıra numarası, GEO sorgusu, toplu yazma | PostgreSQL + Redis |
| `Rota.Notifications.IntegrationTests` | İmza, retry zamanlaması, DLQ, bulkhead, SSRF reddi | PostgreSQL + sahte HTTP alıcı |
| `Rota.OpsApi.IntegrationTests` | GraphQL sorguları, N+1 sayımı, maliyet sınırı, subscription | PostgreSQL + Redis |
| `Rota.Contracts.Tests` | Olay şeması snapshot'ları | Yok |
| `Rota.E2ETests` | Bütün sistem (compose) üzerinde kritik akış | Bütün altyapı |
| `Rota.TestKit` | Container fixture'ları, veri kurucuları (builder), `FakeTimeProvider` yardımcıları, DevAuth başlık yardımcıları | — |

## 3. Kurallar

- **İsimlendirme:** `Metot_Durum_BeklenenSonuç` → `Cancel_WhenPickedUp_ReturnsConflict`.
- **Isolation:** Her test kendi verisini kurar; test başına benzersiz `merchantId`/`courierId` (`Guid.CreateVersion7()`). Tablo temizlemeye dayanmayız; testler paralel ve sırasız çalışabilir.
- **Container paylaşımı:** Container'lar test koleksiyonu başına bir kez başlar (xUnit collection fixture); her test sınıfı aynı DB'yi farklı kimliklerle kullanır.
- **Zaman:** `FakeTimeProvider` ile saat ileri sarılır; 30 sn'lik teklif testi 30 sn beklemez.
- **Asenkron bekleme:** `Task.Delay(1000)` yerine "koşul gerçekleşene kadar, en fazla N sn bekle" yardımcısı. Sabit uyku = flaky test.
- **Race testleri:** `Task.WhenAll` + `Barrier`/`TaskCompletionSource` ile isteklerin gerçekten aynı anda başlaması sağlanır; her race testi döngüyle (ör. 20 tur) çalıştırılır.
- **Assertion:** Shouldly (`result.StatusCode.ShouldBe(409)`).
- **Regression:** Her bug için önce onu yakalayan test, sonra düzeltme; test adı bug'ı anlatır.
- **Coverage:** Hedef değil, ipucu. Domain klasörlerinde düşük coverage, eksik kural testine işarettir.

## 4. Riskli senaryolar (her biri en az bir test)

| # | Senaryo | Tür | Faz |
|---|---|---|---|
| R-1 | 10 kurye, 100 eşzamanlı sipariş → çift atama 0 | Integration (race) | F05 |
| R-2 | Kabul ve zaman aşımı aynı anda | Integration (race) | F05 |
| R-3 | İki Dispatch instance'ı aynı süresi dolan teklifi işlemiyor | Integration | F05 |
| R-4 | İptal ile teslim alma aynı anda | Integration (race) | F06 |
| R-5 | Aynı `Idempotency-Key` ile 10 eşzamanlı istek → 1 sipariş | Integration (race) | F02 |
| R-6 | Aynı olay iki kez → tek yan etki | Integration | F03 |
| R-7 | RabbitMQ kapalıyken oluşan sipariş açılınca tüketiciye ulaşıyor | Integration / kaos | F03 |
| R-8 | Sırası bozuk konum anlık konumu geri almıyor | gRPC integration | F04 |
| R-9 | Webhook alıcısı kapalı → retry → açılınca teslim | Integration | F07 |
| R-10 | Başka işletmenin siparişi → 404; kurye başkasının teklifi → 404 | API | F02, F05, F08 |
| R-11 | Süresi dolmuş takip token'ı reddediliyor | API / GraphQL | F09 |
| R-12 | GraphQL N+1 yok (sorgu sayısı sabit) | Integration | F09 |

## 5. Performans planı

### Hedefler (ilk ölçümden sonra gerekçesiyle güncellenebilir)

| Ölçüm | Hedef | Ölçüm aracı |
|---|---|---|
| Konum alımı (alındı → Redis güncellendi) | 5.000 nokta/sn'de p95 < 50 ms | `CourierFleetSim` (yük üreteci) + OpenTelemetry metrikleri |
| Konumun DB'ye yazılma gecikmesi | p95 < 2 sn | metrik: `received_at` → yazıldı |
| `GetNearbyCouriers` | p95 < 20 ms | gRPC sunucu metriği |
| Sipariş oluşturma | 200 istek/sn'de p95 < 100 ms, hata < %0,1 | k6 |
| Sipariş → ilk teklif kuryeye ulaştı | p95 < 1 sn | metrik (olay zaman damgaları) |
| Webhook ilk deneme gecikmesi | p95 < 2 sn | metrik |

> k6'nın gRPC client-streaming desteği sınırlı olduğu için konum yükünü `CourierFleetSim` üretir (kurye sayısı ve gönderim sıklığı parametreli). HTTP yükünü k6 üretir.

### Senaryolar

| Tür | Senaryo | Amaç |
|---|---|---|
| **Load** | 1.000 kurye × 3 sn'de 1 nokta + dakikada 300 sipariş, 15 dk | Normal yoğunlukta hedefler tutuyor mu? |
| **Stress** | Kurye sayısını 500'er artır (1.000 → 10.000) | İlk kırılan parça hangisi? (Redis mi, toplu yazıcı mı, gRPC mi?) |
| **Spike** | Sipariş hızı 10 sn içinde 10 katına | Kuyruk birikiyor mu, ne kadar sürede eriyor? |
| **Soak** | Load senaryosu 2 saat | Bellek sızıntısı, bağlantı havuzu tükenmesi, partition/temizlik işleri |

### Karşılaştırmalı deneyler (F11)

| Deney | Seçenekler | Neye göre karar? |
|---|---|---|
| Konum geçmişi yazma | EF Core `AddRange` · Npgsql `COPY` + staging | throughput, p95, CPU |
| Toplu yazma boyutu | 100 · 500 · 1.000 · 5.000 nokta | gecikme ile throughput dengesi |
| Teklif zaman aşımı tarama aralığı | 250 ms · 1 sn · 5 sn | DB yükü ile teklif süresi hassasiyeti |

### Rapor şablonu (`perf/reports/<tarih>-<senaryo>.md`)

1. Ortam (CPU, RAM, Docker kaynakları, commit hash)
2. Senaryo parametreleri
3. Sonuç tablosu (p50/p95/p99, throughput, hata oranı)
4. Darboğaz analizi (trace/metrik ekran görüntüsü)
5. Karar ve gerekçe
6. Bilinen sınırlar ("tek makinede ölçüldü; ağ gecikmesi yok")
