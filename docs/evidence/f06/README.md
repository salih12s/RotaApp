# F06 kanıtları (2026-11-12)

| Dosya | İçerik |
|---|---|
| `build.txt` | `dotnet build RotaApp.slnx -warnaserror` → 0 uyarı, 0 hata |
| `all-tests.txt` | `dotnet test RotaApp.slnx` → **611/611** (F05'ten 552; +59) |
| `race-tests-x3.txt` | R-4, `--filter-trait "Category=Race"`, art arda 3 koşu, her seferinde geçti |
| `mutation-checks.txt` | xmin koşulu bozulunca R-4'ün kırıldığı (iki geçersiz ilk denemenin açıklamasıyla) |
| `static-checks.txt` | Kod loga/olaya girmiyor, kimlik token'dan, `DateTime.Now`/`Task.Delay` yok |
| `e2e-50-orders.txt` | 50 siparişlik uçtan uca manuel senaryo, gerçek süreçler, SQL doğrulamaları |

## PASS kriteri → test

| # | Kriter | Durum | Test / kanıt |
|---|---|---|---|
| 1 | **R-4:** iptal ve teslim alma aynı anda → biri 200, diğeri 409 (20 tur) | ✅ | `DeliveryRaceTests.R4_CancelAndPickupAtTheSameInstant_OneGets200AndTheOtherGets409` (her turda tam bir 200 + tam bir 409, saklanan durum kazananla uyumlu, tam bir olay). Mutasyonla kırıldı |
| 2 | 5 yanlış kod → kilit; kilitliyken doğru kod 409; unlock sonrası doğru kod 200 | ✅ | `DeliveryTests.Deliver_FiveWrongCodes_LockTheCode_ThenEvenTheRightCodeIs409_UntilADispatcherUnlocks` (`remainingAttempts` 4…0), `Deliver_AfterUnlock_GivesAllFiveAttemptsBack`; ek: bozuk kod deneme yakmaz, `PickedUp` öncesi deneme yanmaz, 10 paralel yanlış tahmin 5'i geçemez (`..._TenWrongGuessesAtOnce_...`) |
| 3 | Atanmamış kurye `pickup`/`deliver` → 404 | ✅ | `Pickup_ByACourierWhoIsNotAssigned_Returns404AndChangesNothing`, `Deliver_ByACourierWhoIsNotAssigned_Returns404AndBurnsNoAttempt`, `Pickup_OfAnOrderThatHasNoCourierYet_Returns404` |
| 4 | Teslimden sonra Dispatch'te kurye `Available` (≤ 2 sn) | ✅ | `DispatchFlowTests.Delivery_InOrders_FreesTheCourierInDispatchWithinTwoSeconds` (gerçek RabbitMQ + iki veritabanı, `Stopwatch` < 2 sn); `FailedDeliveryAndReturn_FreeTheCourierOnlyWhenTheParcelIsReturned` |
| 5 | Simülatörlerle 50 siparişin tamamı terminal durumda; takılı sipariş yok (SQL); trace ekran görüntüsü | ✅ kısmen | 50/50 terminal: 41 `Delivered`, 6 `Returned`, 3 `Cancelled`; takılı 0 (SQL: terminal olmayan sipariş yok), kurye çakışması 0, serviste hata 0. **Trace ekran görüntüsü yok** (izleme paneli F10'da) |

FAIL sinyalleri: kurye kimliği istek gövdesinden okunmuyor (token + `VisibleTo`) · teslim kodu loga/olaya girmiyor (`static-checks.txt`) · "oku–kontrol et–yaz" ile durum geçişi yok: geçişler `xmin` koşullu `UPDATE`.

## Yapılan işler (roadmap kutuları)

Orders kurye uçları (`pickup`, `deliver`, `delivery-failed`, `returned`) ve dispatcher `delivery-code/unlock` · atanmış kurye kontrolü (sorguda, 404) · atanmış siparişin iptali Dispatch'te kuryeyi serbest bırakıyor (F05 tüketicisi, bu fazda uçtan uca doğrulandı) · `CourierFleet` v3 (kabul ettiğine gider, teslim alır, kodu girer, bazen yanlış, bazen teslim edemez) · `MerchantSim` v1 (sipariş üretir, kodu dosyayla kuryeye iletir, bazılarını iptal eder, sonucu raporlar) · 50 siparişlik senaryo.

## Uçtan uca koşuda ve yazarken yakalanan gerçek hatalar

Dört deneme gerekti; ilk üçü hatayı gösterdi:

- **Deneme 1: 15 kurye teklifi kabul etti, hiçbiri paketi almadı;** kalan 30 sipariş `Unassigned`'a düştü. Sebep simülatörde: `using var ordersHttp` bir `if` bloğunun içindeydi, blok bitince `HttpClient` dispose oldu. Görev sessizce hata verdi. Servislerde hata yoktu.
- **Deneme 2: 37 teslim, 6 iade, 3 iptal, 4 sipariş `Assigned`'da takılı.** Kurye teklifi kabul edince Dispatch hemen 200 döner, Orders ise atamayı `CourierAssigned` olayından bir an sonra öğrenir; o ana kadar `pickup` 404 verir (sipariş kuryeye görünmez). Bu, tasarımın kabul ettiği nihai tutarlılık; sorun simülatörün hiç yeniden denememesiydi. Simülatör artık yalnız 404'te 500 ms aralıkla 20 kez dener (gerçek mobil uygulamanın yapması gereken). Davranış `docs/05`'e yazıldı (45 yeniden deneme gerekti: her kabul ~bir kez).
- **Deneme 2'de benim "çift rezervasyon" SQL'im yanlıştı:** bir kurye zamanla birden çok sipariş kabul eder, eski `Accepted` teklifleri sayınca sahte 12 çıktı. Doğru kontrol Orders geçmişinden "aynı kuryenin atanmış-sipariş aralıkları çakışmasın" (0 çıktı) ve Dispatch'te yalnız `Pending` tek.
- **Deneme 3: hiçbir sipariş ilerlemedi** (hepsi `Created`): geçici RabbitMQ konteynerinin AMQP portu hazır değildi (betik yalnız `rabbitmq-diagnostics ping` bekliyordu). Ürün doğru davrandı: olaylar outbox'ta bekledi, kayıp yok, tüketiciler yeniden bağlanmayı denedi. Betiğe gerçek port beklemesi eklendi.
- **Dispatch'in Redis bağlantısı ilk mesajı ~7 sn tutuyordu** (cross-service teslim testi ≤ 2 sn şartını kırdı): `OfferNotifier` → `ConnectionMultiplexer.Connect()` eşzamanlıdır; Redis'e ulaşılamıyorsa zaman aşımına (5 sn+) kadar bloklar ve bunu ilk kullanan genelde bir mesaj işleyicisi/commit'tir. Düzeltme: bağlantı açılışta arka planda ısıtılıyor, bağlantı zaman aşımı 1 sn. **Gerçek bir üretim riskiydi** (Dispatch Redis kapalıyken açılırsa ilk mesaj gecikirdi); testin 2 sn kuralı yakaladı.
- İlk iki R-4 mutasyonu geçersizdi (EF modeli değişince fixture çöktü); üçüncüsü geçerli (`mutation-checks.txt`).

## Yapılmayanlar / bilinen sınırlar

- **Trace ekran görüntüsü yok** (PASS 5'in son cümlesi): izleme paneli F10'da. Uçtan uca koşuda OTLP uç noktası yoktu.
- **Uçtan uca koşu otomatik test değil,** el ile (betik + SQL). Otomatik uçtan uca (E2E) F12'de.
- **Simülatör kendi içinde otomatik test edilmedi** (ağ yolu): yalnız saf parçaları birim testli (karar kuralları, kod dosyası, seçenekler). Yukarıdaki iki simülatör hatası ancak gerçek süreçlerle görüldü.
- **Kod aktarımı dosyayla** (`codes.jsonl`): gerçek hayattaki işletme → müşteri → kurye zincirinin yerine; iki simülatör ayrı süreç kalsın diye.
- **`q.rota.unrouted` ~190 mesaj:** `CourierStateChanged.v1`'in henüz tüketicisi yok (Ops API, F09). Belgelenmiş bilinen davranış (`05 §3`).
- **`delivery-failed` sonrası kurye `returned`'e kadar meşgul** (paket hâlâ onda); dispatcher bu süreci hızlandıramaz. İş kuralı olarak makul görüldü, PRD'de aksini söyleyen yok.
- **Sipariş `Unassigned` olup hiç atanamazsa** (kurye yok) terminal sayılmıyor; dispatcher yeniden başlatır ya da iptal eder. Senaryoda kurye yeterliydi (20/50 sipariş, ~7 sn/sipariş).
- **F05'ten açık kararlar sürüyor** (yeniden başlatma: aynı iş mi yeni iş mi; soğuk başlangıç deadline'ı; F04 Redis-kapalı konum alımı). F06 bunları değiştirmedi.
- **CI'da henüz koşmadı** (commit sonrası).
