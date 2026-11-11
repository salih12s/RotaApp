# F02 kanıtları (2026-11-11)

| Dosya | İçerik |
|---|---|
| `build.txt` | `dotnet build RotaApp.slnx -warnaserror` → 0 uyarı, 0 hata |
| `all-tests.txt` | `dotnet test RotaApp.slnx` → 290/290 |
| `unit-tests.txt` | `dotnet test tests/Rota.Orders.UnitTests` → 136/136 |
| `integration-tests.txt` | `dotnet test tests/Rota.Orders.IntegrationTests --output Detailed` → 43/43, test test |
| `migrations-list.txt` | `dotnet ef migrations list` → `InitialCreate` (lokal DB'ye bağlanılamadı, aşağıya bkz.) |
| `static-checks.txt` | Saat, kültür, offset ve yasak paket taramaları |

## PASS kriteri → test

| # | Kriter | Test |
|---|---|---|
| 1 | R-5: 10 eşzamanlı aynı anahtar → hepsi 201, aynı gövde, 1 sipariş, 1 outbox | `CreateOrderTests.Create_TenConcurrentRequestsWithSameKey_…` (20 tur; unique ihlali sayacı > 0 → yarış gerçekten oluştu) |
| 2 | Farklı gövde → 409 · anahtarsız → 400 · 15 km üstü → 422 · geçersiz koordinat → 400 + `errors` | `Create_SameKeyWithDifferentBody_…`, `Create_WithoutIdempotencyKey_…`, `Create_BeyondServiceArea_…`, `Create_WithInvalidCoordinate_…` (4 vaka) |
| 3 | Başka işletmenin siparişi → 404 (GET ve cancel) | `Get_OrderOfAnotherMerchant_Returns404`, `Cancel_OrderOfAnotherMerchant_Returns404AndLeavesItUnchanged` |
| 4 | Sipariş kaydından sonra yapay hata → ne sipariş ne outbox | `Create_WhenFailingAfterOrderInsert_…` (interceptor; idempotency anahtarı da kalmıyor, tekrar deneme 201) |
| 5 | Cursor: sayfalama sırasında ekleme → tekrar/atlama yok | `ListOrdersTests.List_WhenOrdersAreCreatedWhilePaging_…` |
| 6 | Migration boş DB'ye uygulanıyor; OpenAPI üretiliyor | `OrderPersistenceTests.Migrations_OnEmptyDatabase_…` (+ `HasPendingModelChanges` = false), `HealthSmokeTests.OpenApi_document_lists_the_order_endpoints` |

Race testleri üç ardışık koşuda da geçti (flaky gözlenmedi); CI'da henüz koşmadı.

## Bilinen ortam sorunu

Bu makinede Windows'a kurulu ayrı bir PostgreSQL servisi `0.0.0.0:5432` dinliyor; `localhost:5432` (IPv4 ve `::1`) Docker yerine ona gidiyor ve `rota` kullanıcısı reddediliyor. Docker içindeki PostgreSQL'de `rota/rota_dev` çalışıyor (`docker exec … psql -h 127.0.0.1` ile doğrulandı). Testleri etkilemez (Testcontainers rastgele port kullanır); VS'ten çalıştırma ve `dotnet ef database update` için yerel servis durdurulmalı ya da compose portu değiştirilmeli.
