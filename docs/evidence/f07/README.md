# F07 kanıtları (2026-11-12)

| Dosya | İçerik |
|---|---|
| `build.txt` | `dotnet build RotaApp.slnx -warnaserror` |
| `all-tests.txt` | `dotnet test RotaApp.slnx` |

## PASS kriteri → test

| # | Kriter | Durum | Test |
|---|---|---|---|
| 1 | **R-9:** alıcı 5 dk kapalı → açılınca bütün olaylar teslim; tekrarlar `id` ile ayıklanabiliyor | ✅ | `ResilienceTests.R9_ReceiverDownForFiveMinutes_...` (20 olay, sahte saat 10 sn'lik adımlarla 5 dk, sonra alıcı açılır: 20/20 teslim, 0 `Dead`, her istek doğrulanabilir imzalı). Tekrar: `DeliveryTests.Worker_ASlowReceiver_CountsAsATimeout_AndTheRepeatCanBeDroppedByTheMerchantUsingTheId` (aynı olay iki kez gelir, tek `id`) |
| 2 | MerchantSim imzayı doğruluyor; değiştirilmiş gövde ve 5 dk'dan eski `t` reddediliyor | ✅ | `WebhookRulesTests` (Rota'nın imzasını bağımsız yazılmış `WebhookVerifier` doğruluyor; değişen gövde, boşluğu farklı gövde, yanlış secret, 301 sn eski, taze `t` + eski imza → hepsi reddedilir; 0/299/300 sn kabul), `DeliveryTests.Worker_ATamperedBodyFailsTheMerchantsCheck` |
| 3 | A işletmesi 10 sn geciktirirken B'nin p95'i < 2 sn | ✅ | `ResilienceTests.Bulkhead_ASlowMerchantDoesNotSlowAFastOne` (gerçek zaman; A 3 sn/istek, 12 A + 40 B olayı; B p95 < 2 sn). **Düzeltme öncesi bu test 13,2 sn ile kırıldı** (bkz. aşağıda), `..._NeverMoreThanFourRequestsInFlightToOneMerchant` |
| 4 | `https://127.0.0.1`, `http://10.0.0.5` gibi URL'ler 400 | ✅ | `SubscriptionAndHistoryTests.Put_WithAnUnsafeOrInvalidUrl_...` (9 vaka), `localhost` adı (çözümlenince loopback) ve çözümlenmeyen ad 400; 29 adres/şema birim vakası (`WebhookRulesTests`) |
| 5 | Deneme limiti aşılınca `Dead`; replay sonrası teslim | ✅ | `DeliveryTests.Worker_AfterSevenFailures_...` (7 istek, sekizinci yok), `SubscriptionAndHistoryTests.Replay_OfADeadDelivery_Returns202_AndTheDeliveryIsSentAgain` |
| 6 | Servis yeniden başlatıldıktan sonra mevcut secret'larla imzalama çalışıyor | ✅ | `ResilienceTests.Restart_ExistingSecretsStillSign_...` (aynı veritabanı + aynı anahtar dizini, yeni host); karşı deney: `Restart_WithoutTheKeyRing_...` (boş anahtar dizini → secret okunamaz, teslimat yanlış imzalanmak yerine görünür biçimde başarısız) |

## Yazarken yakalanan gerçek hata

- **Bulkhead tek başına yetmiyordu.** İşletme başına 4 yuva doğruydu, ama işçi bir tur bitmeden yenisini başlatmıyordu ve bir tur, yavaş A'nın 3 sn'lik isteklerini bekliyordu. B'nin 40 olayı turlara bölünüp (tur başına işletme başına 8 satır) her turda 3 sn beklediği için B'nin p95'i **13,2 sn** çıktı. Düzeltme: işçi turu beklemeden yeni satır almaya devam ediyor (kira çift almayı engelliyor); en çok 256 satır uçuşta. Testler için `RunOnceAsync` hâlâ bekliyor.

- **Aynı zamanlama testi tam pakette kırıldı:** bulkhead testi tek başına geçiyordu ama tüm paketle (7 test projesi paralel) B'nin p95'i 2,18 sn çıktı: sürecin ilk isteği (JIT, bağlantı kurulumu) ölçüme giriyordu. Test, ölçümden önce B'ye bir ısınma teslimatı gönderecek şekilde sağlamlaştırıldı (eşik hâlâ 2 sn); sonra 3 ayrı ve tam pakette geçti. Eşik gevşetilmedi.

## Yapılmayanlar / bilinen sınırlar

- **RabbitMQ üzerinden uçtan uca (Orders → Notifications → alıcı) otomatik test yok;** tüketici işleyicileri doğrudan, tüketicinin yaptığı gibi transaction içinde çağrılıyor. İnbox/yeniden deneme altyapısı F03'te testli.
- **Bulkhead ve devre kesici instance başına bellekte;** birden çok Notifications instance'ında sınır toplamda 4×N. Birden çok instance ile test yok.
- **Data Protection anahtarları diskte, şifresiz** (Windows'ta DPAPI ile korunabilir; containerda paylaşılan volume gerekir, F12). Anahtar yedeklemesi/rotasyonu kapsam dışı.
- **Devre kesici yarı-açık durumda tek deneme** ayrımı yok: süre dolunca bir sonraki tur normal istek atar, ilk hatada tekrar açılır (birim testli).
- **Abonelik silme/pasifleştirme ucu yok** (docs/05'te listelenmedi); `is_active` alanı var ama ucu yok.
- **`CourierStateChanged` tüketicisi yalnız adı kopyalar;** adı olmayan kurye için `displayName: null`.
- **CI'da henüz koşmadı** (commit edilmedi).
