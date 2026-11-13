# F09 kanıtları

| Dosya | Komut | Sonuç |
|---|---|---|
| `build.txt` | `dotnet build RotaApp.slnx -warnaserror -v q` | 0 uyarı, 0 hata |
| `ops-tests.txt` | `dotnet test tests/Rota.OpsApi.IntegrationTests` | 67 test, 67 geçti |
| `all-tests.txt` | `dotnet test RotaApp.slnx` | 939 test, 938 geçti. Düşen: `Orders...DispatchFlowTests.Delivery_InOrders_FreesTheCourierInDispatchWithinTwoSeconds` (gerçek saate bağlı 2 sn sınırı; tek başına 3/3 geçti, F09 koduyla ilgisi yok) |

## PASS kriterleri

| # | Kriter | Durum | Kanıt (test) |
|---|---|---|---|
| 1 | R-12: 50 siparişlik `orders { courier { lastLocation } }` → en fazla 2 DB sorgusu + 1 gRPC | ✅ | `DataLoaderTests` (EF komut sayacı + gerçek gRPC soketindeki sahte Tracking; DataLoader yerine tek tek sorgu atınca düşüyor) |
| 2 | R-11: süresi dolmuş / sahte / başka audience / çöp token reddi; `Delivered` sonrası akış kesilir | ✅ | `SubscriptionTests`: `An_expired_link...`, `A_link_signed_with_somebody_elses_key...`, `...wrong_audience_or_issuer...`, `A_garbage_link...` (3 girdi), `A_valid_link_for_an_order_that_cannot_be_followed...` (Created/Delivered/Cancelled), `The_stream_ends_when_the_order_is_delivered...`, `A_link_that_expires_while_watching...`, `A_valid_link_follows_the_orders_own_courier_and_nobody_elses` |
| 3 | Eski sürümlü olay okuma modelini geri almıyor | ✅ | `ReadModelTests` (`order_version <` korumasını kaldırınca 2 test düşüyor) |
| 4 | Maliyet sınırını aşan sorgu çalıştırılmadan reddediliyor | ✅ | `ResilienceAndCostTests.A_query_over_the_cost_limit_is_refused_before_anything_runs` (Tracking'e 0 çağrı, `data` yok), `The_panels_own_50_order_screen_is_still_allowed`, derinlik: `A_query_nested_deeper_than_the_depth_limit_is_refused` |
| 5 | İki Ops API instance'ı: B'ye bağlı subscription, A'nın tükettiği olayı alıyor | ✅ gerçek Redis, gerçek WebSocket | `SubscriptionTests.A_panel_on_instance_B_hears_a_status_change_that_instance_A_consumed` |

## Mutation ile doğrulananlar (kodu bozup testin düştüğünü gördük)

| Bozulan | Düşen test |
|---|---|
| Abonelik kurulurken `DispatcherOnly` kontrolü kaldırıldı | `The_dispatcher_feeds_refuse_everyone_else` (3 kimlik) |
| Sipariş `Delivered` olunca akışı kesen koşul kaldırıldı | `The_stream_ends_when_the_order_is_delivered...` |
| Eski olayda da canlı bildirim gönderildi (`updated > 0` kaldırıldı) | `An_old_event_that_changes_nothing_is_not_announced` |
| Gateway: `AnonymousOnly` koşulu kaldırıldı | `A_signed_in_dispatcher_is_not_throttled_by_the_anonymous_limit...` |
| `ServiceCredentials` her çağrıda token yeniliyor | `The_token_is_reused_until_shortly_before_it_expires_then_renewed` |

## Testlerin bulduğu hatalar (düzeltildi)

1. **Güvenlik:** `[Authorize]` subscription alanında abonelik kurulurken değil olay gelirken çalışıyor; yetkisiz biri dispatcher akışını açıp bekleyebiliyordu. Abonelik kurulurken açık kontrol eklendi.
2. Anonim girdi `a.b.c` token doğrulayıcıda istisna fırlatıyordu (crash); artık `invalid-tracking-token`.
3. Kendi hatalarımız Hot Chocolate'in "Unexpected Execution Error"ına dönüşüyor (Development'ta stack trace sızdırıyor); `OpsErrorFilter` ile kodlu hata.
4. Ops API → Tracking çağrısı belgedeki 300 ms / devre kesici / "konumsuz yanıt" kuralına uymuyordu (2 sn, bozulunca hata). Düzeltildi ve test edildi.
5. Gateway `anonymous-tracking` kuralı `/graphql` yolundaki **giriş yapmış** dispatcher'ı da IP başına 10/dk'ya sokardı; `AnonymousOnly` bayrağı eklendi.

## Alınan kararlar

- `restartDispatch`: Dispatch'in mevcut davranışı kullanıldı (aynı iş, yeni deneme bütçesiyle yeniden aranır; `Searching`'e döner). F05'ten açık kalan "aynı iş mi yeni iş mi" sorusu bu haliyle fiilen "aynı iş" kararı olmuş durumda: **onayın gerekir.**
- `/graphql` gateway'de anonim rota (docs/06 §2): müşteri hesapsız izliyor; yetkiyi Ops API alan bazında veriyor.
- Canlı bildirim (`orderStatusChanged`) en iyi çabadır: Redis kapalı/yavaşsa mesaj başarısız sayılmaz (okuma modeli zaten doğru), 1 sn'de vazgeçer.

## Test edilmeyenler (dürüst liste)

- **Gateway üzerinden WebSocket** uçtan uca denenmedi (rota yazıldı, HTTP kısmı testli; TestServer YARP WebSocket yükseltmesini taşımıyor, gerçek Kestrel + gerçek Ops API'li test yazılmadı).
- **Tarayıcıdan** WebSocket: tarayıcı `Authorization` başlığı gönderemez; token'ı `connection_init` yükünden / `access_token` sorgusundan okuma **yazılmadı**. Başlık gönderebilen istemciler (ve DevAuth testleri) çalışır.
- Ops API'nin **gerçek Keycloak JWT**'siyle GraphQL yetkisi denenmedi (yetki matrisi DevAuth ile; JWT doğrulaması F08'de `AddRotaAuth` ile ortak ve orada testli). Dispatch'e iletilen gerçek `Authorization` başlığı yalnız sahte Dispatch'e karşı sınandı.
- JWKS anahtar rotasyonu (bilinmeyen `kid` → yeniden çekme) kodda var, testi yok.
- `orderTracking` ilk bağlanmada son bilinen konumu göndermiyor (yalnız yeni konumlar); kurye yeniden atanırsa (`Assigned` başka kuryeyle) akış eski kuryeyi izlemeye devam eder.
- Redis kapalıyken subscription davranışı (abonelik hatayla düşer; yeniden bağlanınca yeni abonelik gerekir) testlenmedi.
- Dispatch → Ops API arasında gerçek RabbitMQ ile tüketici zinciri yok (tüketiciler doğrudan çağrılıyor; F03'ün mesajlaşma testleri ayrı).
- Bilinen kararsız testler: `ResilienceTests.Bulkhead_...` (F07) ve `DispatchFlowTests.Delivery_InOrders_FreesTheCourier...` (gerçek saat, 2 sn). Son tam koşuda ikincisi düştü.
