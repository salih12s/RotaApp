# F05 kanıtları (2026-11-12)

| Dosya | İçerik |
|---|---|
| `build.txt` | `dotnet build RotaApp.slnx -warnaserror` → 0 uyarı, 0 hata |
| `all-tests.txt` | `dotnet test RotaApp.slnx` → **552/552** (F04'ten 427; +125) |
| `dispatch-integration-test-list.txt` | `Rota.Dispatch.IntegrationTests` → 89 test, test test (gerçek PostgreSQL + Redis + sahte Tracking gRPC sunucusu) |
| `race-tests-x3.txt` | `--filter-trait "Category=Race"` art arda 3 koşu, her seferinde 4/4 |
| `mutation-checks.txt` | Kodu bozup testin kırıldığının kanıtı (R-1, R-2, R-3) |
| `static-checks.txt` | FAIL sinyali taramaları: Redis'te kurye durumu, "oku–kontrol et–yaz", `Task.Delay`, saat, kültür, yasak paket, doğrudan publish |
| `smoke-simulator-v2.txt` | Gerçek süreçlerle 60 sn duman testi: 30 kurye, 25 iş, 21 atama, 0 çift rezervasyon |
| `chaos-redis-stop-start.txt` | **El ile** Redis durdur/başlat deneyi (PASS 6), otomatik test değil |

## PASS kriteri → test

| # | Kriter | Durum | Test / kanıt |
|---|---|---|---|
| 1 | **R-1:** 10 kurye + 100 eşzamanlı sipariş, 20 tur → çift rezervasyon yok (SQL ile) | ✅ | `RaceTests.R1_HundredConcurrentOrdersAndTenCouriers_...`. Her turda 100 arama aynı anda (`TaskCompletionSource` kapısı), tam 10 teklif + 90 "aday yok"; SQL: aktif teklifte kurye/sipariş başına >1 yok, `Offered`/`OnDelivery` kurye satırı teklif tablosuyla uyumlu; sonra 10 kurye aynı anda kabul eder, değişmezler tekrar. Mutasyon M1 ile kırıldığı gösterildi |
| 2 | **R-2:** Kabul ve zaman aşımı aynı anda → yalnız biri (20 tur) | ✅ | `R2_AcceptAndTimeoutAtTheSameInstant_ExactlyOneIsApplied` (XOR + durum tutarlılığı) ve `..._NeverProduceBothAssignmentAndFailureEvents` (5. denemede: aynı siparişe hem `CourierAssigned` hem `DispatchFailed` yok). İki sunucu saati 1 ms farklı: iki zaman koşulu da aynı anda doğru, kararı yalnız `status = 'Pending'` verir. M2 ile kırıldı |
| 3 | **R-3:** İki zaman aşımı işçisi aynı anda → her teklif bir kez işleniyor | ✅ | `R3_TwoTimeoutWorkersAtTheSameTime_ProcessEveryOfferExactlyOnce` (5 tur × 40 teklif, küçük batch'lerle; toplam işlenen = 40, kurye `version` tam +1, her teklif için tam 1 `CourierStateChanged`). M3 ile kırıldı. Not: iki işçi = aynı süreçte iki bağımsız `DbContext`/bağlantı (iki ayrı Dispatch *süreci* değil; veritabanı açısından aynı) |
| 4 | Teklif akışa ≤ 1 sn; kopukken oluşan teklif yeniden bağlanınca geliyor | ✅ | `OfferStreamTests.Subscribe_ThenAnOfferIsCreated_ArrivesWithinOneSecond` (commit → Redis → akış → okuma, `Stopwatch` < 1 sn), `Subscribe_AfterAnOfferWasCreatedWhileDisconnected_DeliversItOnReconnect`; ek: itme kaybolursa yoklama (`..._WhenThePushIsLost_...`), geri çekme (`expired`, `order-cancelled`), kalp atışı |
| 5 | Tracking yavaşken (1 sn gecikme) deadline devreye giriyor, iş `Searching`'de kalıp tekrar deneniyor | ✅ | `OfferingTests.TryOffer_WhenTrackingIsSlow_GivesUpAtTheDeadlineAndKeepsTheJobSearching` (gerçek gRPC soketi, 200 ms deadline; çağrı < 800 ms, `next_search_at` = +20 sn, deneme sayısı 0). Yeniden deneme: `..._RetriesExactlyOnceAndSucceeds`, `..._RetriesOnlyOnce`, `..._DoesNotRetry` (UNAVAILABLE dışı) |
| 6 | Redis durdurulunca atama ertelenir, çift atama yok; Redis gelince devam | ✅ (kısmen el ile) | Otomatik: `TryOffer_WhenTrackingIsUnavailable_PostponesThenAssignsOnceTrackingIsBack_WithoutDoubleAssignment` (Redis kapalıyken Tracking'in cevabının yerine konan `UNAVAILABLE`). **Gerçek Redis durdur/başlat el ile yapıldı** (`chaos-redis-stop-start.txt`): kapalıyken yeni iş ertelendi, dönünce 10/12 atandı, 0 çift rezervasyon. Otomatik teste çevrilmedi |
| 7 | Elle atamada müsait olmayan kurye → 409 | ✅ | `DispatcherCommandTests.ManualAssign_BusyCourier_Returns409CourierNotAvailableAndChangesNothing` (+ offline, teklif tutan kurye; iş değişmez, geri alma kanıtı) |

FAIL sinyalleri: kurye durumu Redis'te **değil** (Redis yalnız Pub/Sub + ping; `static-checks.txt` 1–2) · "oku–kontrol et–yaz" rezervasyon **yok** (kurye geçişleri yalnız koşullu `UPDATE`; iş geçişi: durumu domain karar verir, yazma `status` + `offer_attempts` koşuluyla) · teklif süresi `Task.Delay` ile **beklenmiyor** (zaman aşımı işi `expires_at <= şimdi` sorgusu; `Task.Delay` Dispatch'te yok) · testler gerçek 30 sn **beklemiyor** (`FakeTimeProvider`).

## Yapılan işler (roadmap kutuları)

Tablolar + migration (`couriers`, `offers`, `dispatch_jobs` yeni sütunlar) · vardiya uçları · aday arama (Tracking, 200 ms) + `CandidateRanker` · teklif verme (koşullu update + teklif + outbox tek transaction, commit sonrası Redis) · `SubscribeOffers` · kabul/ret → `CourierAssigned.v1` · zaman aşımı işi (`SKIP LOCKED`), 5 deneme → `DispatchFailed.v1`, aday yoksa 20 sn'lik erteleme / 3 dk sonra `Unassigned` · elle atama + yeniden başlatma · `OrderStatusChanged` tüketicisi · Orders `CourierAssigned`/`DispatchFailed` tüketicileri · `CourierFleetSim` v2 (`--offers`) · metrik `rota.dispatch.time_to_first_offer`.

## Çapraz servis testi (RabbitMQ)

`Rota.Orders.IntegrationTests/Messaging/DispatchFlowTests` (4 test, gerçek RabbitMQ + iki veritabanı): elle atama → Orders'ta `Assigned` · Orders'ta iptal → Dispatch'te kurye serbest · `DispatchFailed` → `Unassigned` · iptal edilmiş siparişe gelen `CourierAssigned` yok sayılır, DLQ'ya gitmez.

## Yazarken ve denerken yakalanan gerçek hatalar

- **Sahte Tracking sunucusu `appsettings.json`'u okudu** (test klasörüne kopyalanan Dispatch ayarı): sabit Kestrel portu 5202'ye her sunucu bağlanmaya çalıştı; 89 testin 75'i "address already in use" ile düştü (ilk açılan sunucu portu tuttu). Çözüm: sahte sunucunun yapılandırma kaynakları temizlendi.
- **EF Core, kurucuyla kurulmuş bir projeksiyonun üstündeki `Where`'i çeviremedi** (`OfferView.Status`): `MyOffers` 500, akış hata. Süzme projeksiyondan önceye taşındı (`OfferViews(filter)`).
- **İlk gRPC çağrısı (JIT) 200 ms deadline'ı aşıyordu** (test ortamı): testlerde varsayılan deadline gevşetildi, deadline testi gerçek 200 ms ile koşuyor. Aynısı gerçek süreçte de görüldü (bilinen sınırlar).
- **Aynı kuryeye aynı siparişin teklifi tekrar tekrar gidiyordu:** teklifi cevaplamayan en yakın kurye `Available`'a dönünce yine en yakın aday olurdu, 5 deneme tek kuryeye giderdi. Bir kurye bir siparişin teklifini bir kez alır (`OfferingService.RankAsync`); `Expire_ThenSearch_...` ve `FiveUnansweredOffers_...` testleri.
- **Simülatör v2, gerçek süreçlerle denenince iki hata verdi** (birim testlerle yakalanamazdı): (a) duruşta iptal edilen akış `RpcException(Cancelled)` fırlatıp süreci düşürüyordu; (b) bir `GrpcChannel`'a verilen `SocketsHttpHandler` başka kanal/`HttpClient` ile paylaşılamıyor ("Unable to get subchannel"), kuryeler vardiya açamıyordu.
- **Docker Desktop kapalıydı** (testler Testcontainers ister): başlatıldı.

## Yapılmayanlar / bilinen sınırlar

- **PASS 6'nın gerçek Redis durdur/başlat hâli otomatik test değil**, el ile yapıldı. Otomatik test Redis yerine Tracking'in `UNAVAILABLE` cevabını kullanır.
- **Soğuk başlangıç:** süreç ayağa kalkınca ilk `GetNearbyCouriers` çağrıları (JIT) 200 ms deadline'ı aşabiliyor; ilk siparişler 20 sn ertelenir (kaos denemesinde dalga 1 böyle ertelendi). Doğruluk etkilenmez; çözüm adayı: açılışta bir ısınma çağrısı. Ölçüm F11'de.
- **Redis kapalıyken Tracking'in konum alımı hata veriyor** (kaos denemesinde simülatör 63 akış hatası saydı); `docs/07 §7` "Redis kapalı: konum alımı geçmişe yazmaya devam eder" der. Bu F04 davranışı, bu fazda incelenmedi/değiştirilmedi. Salih'e bildirildi.
- **Dispatch'in kendi Redis'i kapalıyken teklif oluşturma/akış yoklaması otomatik test edilmedi.** Tasarım: yayın `FireAndForget` + akış yoklaması; kaos denemesinde Dispatch'in Redis bağlantısı da düştü/döndü ve akışlar teklifleri aldı (simülatör 10 kabul), ama bu bir test değil.
- **Teslim alma/teslim yok (F06):** kabul eden kurye `OnDelivery`'de kalır; yalnız Orders `Delivered`/`Returned`/`Cancelled` olayı onu serbest bırakır (testli). Simülatör v2 kabul ettikten sonra "teslim etmez".
- **`time_to_first_offer` metriği tanımlı ve kayıt altında ama bir toplayıcıda (OTLP) görülmedi** (F10).
- **İki ayrı Dispatch *süreci* ile test yok.** R-3 aynı süreçte iki bağımsız `DbContext`; `docs/07 §7` "iki instance testi" ayrı bir ortam ister (F12).
- **Gerçek RabbitMQ üzerinden Dispatch'in kendi arama→teklif→kabul akışı** (Tracking dahil) uçtan uca otomatik test edilmedi; parçalar ayrı test edildi (arama: sahte Tracking; kabul→Orders: `DispatchFlowTests`).
- **İptal olayının, siparişin `OrderCreated`'ını geçmesi** (farklı kuyruklar): tüketici hata verip yeniden denemeye bırakır (3 deneme: 5 sn, 30 sn, 2 dk), sonra DLQ. Testle yalnız "hata fırlatır" gösterildi, gerçek geç gelen `OrderCreated` ile yeniden deneme zinciri denenmedi.
- **Belge çelişkisi (Salih'in kararı bekliyor):** `02 §5.4` "yeniden başlatınca yeni iş", `04` `dispatch_jobs.order_id` PRIMARY KEY. Aynı iş yeniden açıldı; ayrıntı ve geri dönüş yolu `02 §5.4` notunda.
- **`docs/04`'e eklenenler** (belge `dispatch_jobs`'ta adres/varış, `couriers`'ta `version` tutmuyordu; `05`'teki `Offer.pickup/dropoff` ve `courierVersion` için gerekliydi): `pickup_address`, `dropoff_lat/lon/address`, `couriers.version`.
- **Paket:** `Grpc.Net.ClientFactory 2.85.0` eklendi (grpc-dotnet ile aynı sürüm hattı, Apache-2.0, .NET 10 uyumlu, nuget.org'da son kararlı sürüm).
- **CI'da henüz koşmadı** (commit sonrası).
