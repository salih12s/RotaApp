# F08 kanıtları

| Dosya | Komut | Sonuç |
|---|---|---|
| `build.txt` | `dotnet build RotaApp.slnx -warnaserror -v q` | 0 uyarı, 0 hata |
| `all-tests.txt` | `dotnet test RotaApp.slnx` | 864 test, 864 geçti (F07'de 742; +122 Auth) |
| `auth-tests.txt` | `dotnet test RotaApp.slnx --filter-trait "Category=Auth"` | 122 geçti. Çıktıdaki "error: 9": eşleşen testi olmayan 9 proje "zero tests ran" (çıkış kodu 8) döndürüyor, hata değil |

## PASS kriterleri

| # | Kriter | Durum | Kanıt (test) |
|---|---|---|---|
| 1 | R-10: token yok 401, yanlış rol 403, başkasının kaynağı 404 | ✅ | `RoleMatrixTests` (3 servis, 21 uç × 5 kimlik; izinli rol yalnız Orders'ta), `TokenValidationTests.A_merchant_creates_an_order_and_sees_it_but_another_merchant_gets_404`, `TrackingGrpcAuthTests` |
| 2 | Gateway atlanıp servise token'sız istek → 401 | ✅ REST (Orders, Dispatch, Notifications), gRPC (Tracking) | `TokenValidationTests.Bypassing_the_gateway_...`, `TrackingGrpcAuthTests` |
| 3 | İki gateway'e bölünen 2× istek → ≈ sınır kadarı geçer | ✅ (40 istekten 20–22'si geçiyor) | `GatewayTests.Two_gateway_instances_share_one_limit` |
| 4 | `DevAuth` Production'da başlamıyor | ✅ | `StartupRuleTests` (birim + gerçek Orders `Program`) |

## Test edilmeyenler (dürüst liste)

- Gateway üzerinden **gRPC** ve **WebSocket** proxy'si uçtan uca denenmedi; rotalar yapılandırıldı. WebSocket rotası Ops API ile F09'da.
- Dispatch'in gRPC ucu (`CourierOffers`) gerçek JWT ile denenmedi (Tracking'inki denendi; aynı `AddRotaAuth`).
- İzinli rolün "geçebildiği" yalnız Orders'ta kanıtlı; Dispatch/Notifications'ta veritabanı tabloları olmadığı için yalnız red durumları sınandı.
- Süresi dolmuş token ve Keycloak anahtar rotasyonu testlenmedi (JwtBearer'ın kendi davranışı; discovery belgesinden anahtar alınıyor).
- Simülatörlerin servislere karşı uzun koşusu yapılmadı; yalnız kimlikleri (5 merchant, 50 kurye, servis) gerçek realm'e karşı sınandı.
- Redis kapalıyken limitleyicinin "fail open" davranışı kodda var, testi yok.
- Bilinen kararsız test (F07'den): `ResilienceTests.Bulkhead_ASlowMerchantDoesNotSlowAFastOne`, gerçek saatle p95 < 2 sn; F08 koşularında 5 koşunun ~1'inde düştü, son tam koşuda geçti.
