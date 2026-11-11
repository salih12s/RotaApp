# F04 kanıtları (2026-11-11)

| Dosya | İçerik |
|---|---|
| `build.txt` | `dotnet build RotaApp.slnx -warnaserror` → 0 uyarı, 0 hata |
| `all-tests.txt` | `dotnet test RotaApp.slnx` → 427/427 (F03'ten 306; +121) |
| `tracking-unit-tests.txt` | `Rota.Tracking.UnitTests` → 62/62, test test |
| `tracking-integration-tests.txt` | `Rota.Tracking.IntegrationTests` → 59/59, test test (gerçek Redis + PostgreSQL) |
| `static-checks.txt` | FAIL sinyali taramaları: sınırsız kuyruk, `SaveChanges`, mesajdan kimlik, saat, kültür, yasak paket |
| `../../perf/reports/2026-11-11-tracking-baseline.md` | Taban çizgisi ölçümü (kriter 6) |

## PASS kriteri → test

| # | Kriter | Durum | Test |
|---|---|---|---|
| 1 | R-8: sırasız nokta anlık konumu geri almıyor ama geçmişe yazılıyor | ✅ | `CurrentPositionTests.Stream_OutOfOrderPoint_DoesNotRollBackTheCurrentPosition` (Redis) + `HistoryTests.OutOfOrderPoint_IsWrittenToHistory_ButDoesNotMoveTheCurrentPosition` (seq 1, 3, 2 → 3 satır, güncel konum seq 3) |
| 2 | Aynı nokta iki kez → DB'de tek satır | ✅ | `HistoryTests.SamePointSentTwice_IsOneRowInHistory` (bir akışta iki kez + yeni bağlantıda bir kez → tek satır) |
| 3 | `GetNearbyCouriers` doğru küme, doğru sıra; 30 sn'den eski yok | ✅ | `NearbyCouriersTests`: 500/1.500/3.000 m'deki kuryeler, yarıçap 2 km → ilk ikisi artan sırada, mesafeler ±%1; 30. sn dahil, 31. sn hariç; bayat yakın kuryeler `limit`'i doldurmuyor |
| 4 | Aynı kurye için iki eşzamanlı akış konumu geri almıyor | ✅ | `CurrentPositionTests.Stream_TwoConcurrentStreamsOfOneCourier_NeverMoveTheCurrentPositionBackwards` (20 tur × 2 akış × 100 nokta; yayınlanan seq dizisi hep artan ve tekrarsız) |
| 5 | Bölüm işi `FakeTimeProvider` ile: yeni bölümler oluşuyor, 30 günden eskiler siliniyor | ✅ (not) | `PartitionMaintenanceTests`. Not: bakım sınıfı "şimdi"yi **parametre** olarak alır (proje kuralı: zaman parametre geçer), testler tarihi doğrudan verir; `FakeTimeProvider` yalnız servis açılışı testinde (`ServiceStartup_CreatesThePartitions_BeforeAnyTraffic`) ve `PartitionPlan`/`LocationBatcher` birim testlerinde kullanıldı |
| 6 | Taban çizgisi ölçümü `perf/reports/` altında | ✅ | `2026-11-11-tracking-baseline.md` |

FAIL sinyalleri: sınırsız kuyruk **yok** (`LocationBatcher` sınırlı, dolunca yazan bekler; birim testle gösterildi) · sıcak yolda `SaveChanges` **yok** (`COPY`) · kurye kimliği **mesajdan okunmuyor** (`LocationPoint` mesajında kimlik alanı yok; `GetCourierId()` token'dan).

## Testlerin gerçekten bir şey kanıtladığının kontrolü (mutasyon)

| Bozulan şey | Beklenen | Sonuç |
|---|---|---|
| Lua script'inde kontrol + yazma ayrıldı (kontrol C#'a taşındı, atomiklik bozuldu) | Yarış testi kırılmalı | **3/3 koşuda kırıldı** (`should be unique`: iki akış aynı seq'i kabul edip iki kez yayınladı) |
| `INSERT ... ON CONFLICT DO NOTHING` kaldırıldı | Tekrar-satır testi kırılmalı | **Kırıldı** (toplu yazma çift anahtar hatası verip düşürüldü) |
| `GetNearbyCouriers` aramasında "yeterli taze bulunana kadar genişlet" döngüsü kaldırıldı ("önce en yakın N, sonra filtre") | Bayat-yakın-kurye testi kırılmalı | **Kırıldı** (yalnız o test, 18'in 1'i) |

Her mutasyondan sonra kod geri yüklendi ve kodda `MUTATION` kalıntısı olmadığı doğrulandı (0).

## Yazarken yakalanan gerçek hatalar

- **Dondurulmuş sahte saat, toplu yazma zamanlayıcısını kilitledi.** Test fabrikası servisin `TimeProvider`'ını sabit saatle değiştiriyor; toplu yazıcının "500 ms bekle" penceresi de aynı saati kullandığı için hiç dolmuyordu, yalnız 1.000 noktayı dolduran batch yazılıyordu. `OutOfOrder` testi bu yüzden **tesadüfen** geçmişti (yanındaki 2.500 noktalık test onu da dışarı itmişti); tek başına koşturunca kırıldı. Düzeltme: iş saati `TimeProvider`'dan, altyapı zamanlayıcıları gerçek saatten (07 §3'e yazıldı).
- **`Channel.Complete()` ikinci kez çağrılınca fırlatıyor** (servis kapanışında). `TryComplete()` yapıldı.
- **Redis GEO enlem sınırı:** ±85,05° ötesini reddediyor (gerçek bir enlem olsa bile). Bu hata servis tarafında 500 olurdu; istemci hatası (`INVALID_ARGUMENT`) olarak ele alındı ve testle sabitlendi.

## Yapılmayanlar / bilinen sınırlar

- **Servis VS'ten (F5) çalıştırılmadı.** Ölçüm için servis gerçek süreç olarak (`dotnet run -c Release`) çalıştırıldı, ama yerel PostgreSQL port çakışması (F02 raporu) yüzünden **geçici** Docker PostgreSQL/Redis kullanıldı; işi bitince silindi.
- **Bayat kuryeleri Redis GEO'dan silen temizlik işi yok.** `docs/04` "5 dk'dan eskileri GEO'dan siler" der; F04 listesinde yoktu ve yapılmadı. Doğruluk etkilenmez (sorgu zaten `lastseen` ile filtreler), ama bir daha hiç konum göndermeyen kurye GEO'da kalır.
- **`06 §3` konum hız sınırı yok:** "kurye başına saniyede en çok 5 nokta, fazlası düşürülür" ve mesaj başına boyut sınırı uygulanmadı (F04 listesinde değildi).
- **Birden çok Tracking örneği test edilmedi.** Staging tablosunun örnekler arası güvenliği (her yazma tek transaction, `DELETE`) tasarım olarak savunuldu ama iki örnekle denenmedi. Bölüm bakımı için iki eşzamanlı çalışma testi var (advisory lock).
- **`/health/ready` yalnız Redis'i kontrol eder,** PostgreSQL'i değil (geçmiş en iyi çabadır). Orders/Dispatch için de DB hazırlık kontrolü hâlâ yok.
- **gRPC istemci tarafı (deadline, retry) yok:** `GetNearbyCouriers` için 200 ms deadline ve 1 retry (`05 §2`) Dispatch istemcisi yazılınca (F05) uygulanacak.
- **Trace/dashboard'da gRPC izleri incelenmedi** (yalnız ASP.NET Core span'leri otomatik; gRPC'ye özel öznitelikler bakılmadı).
- **Simülatör harita çizmez** (F13).
- **CI'da henüz koşmadı** (commit edilmedi).
- Tracking Redis Cluster'da çalışmaz (script anahtarları aynı slot'ta olmalı); tek düğümde sorun yok (04'e yazıldı).
