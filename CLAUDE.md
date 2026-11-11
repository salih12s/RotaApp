# Rota — Claude Code ajanları için proje kuralları

Rota; işletmelerin siparişlerini konum bazlı olarak en uygun kuryeye otomatik atayan, kurye konumunu gerçek zamanlı işleyen ve teslimat durumunu güvenilir şekilde bildiren, mikroservis tabanlı bir son-mil teslimat platformudur.

Bu repo bir **portföy ve öğrenme projesidir**. Salih (geliştirici) mimariyi, kararları ve çıktıların doğruluğunu denetler; kodun çoğunu ajanlar yazar. Amaç yalnızca çalışan kod değil, Salih'in her kararı **anlayıp mülakatta savunabilmesidir**.

## Önce oku

Herhangi bir işe başlamadan önce ilgili belgeyi oku. Belge ile kod çelişirse **belge kazanır**; çelişkiyi Salih'e bildir, sessizce kodu "düzeltme".

| Belge | Ne zaman? |
|---|---|
| [docs/README.md](docs/README.md) | Her zaman: belge haritası ve açık kararlar |
| [docs/08-roadmap.md](docs/08-roadmap.md) | Her faz başında: hedef, kapsam, PASS/FAIL |
| [docs/02-domain.md](docs/02-domain.md) | Durum makinesi / iş kuralı yazarken |
| [docs/03-architecture.md](docs/03-architecture.md) | Servisler arası iletişim, proje yapısı |
| [docs/04-data-model.md](docs/04-data-model.md) | Tablo, migration, Redis anahtarı |
| [docs/05-contracts.md](docs/05-contracts.md) | REST, gRPC, olay, webhook, GraphQL sözleşmeleri |
| [docs/06-security-and-errors.md](docs/06-security-and-errors.md) | Auth, yetki, hata kodları, dayanıklılık |
| [docs/07-testing-and-performance.md](docs/07-testing-and-performance.md) | Test yazarken, ölçüm yaparken |
| [docs/09-adr.md](docs/09-adr.md) | Bir karara itiraz etmeden ya da yeni karar almadan önce |

## Çalışma şekli

1. **Faz disiplini.** Aynı anda tek faz. Aktif faz `docs/08-roadmap.md` içindeki "Durum" tablosunda yazar. Fazın kapsamı dışındaki işi yapma; gerekli görürsen öner ve Salih'e sor.
2. **Koddan önce açıkla.** Her fazın ve her büyük adımın başında kısa, sade Türkçeyle: bu adım hangi problemi çözüyor, neden bu yöntem, hangi hatayı önlüyor. Yeni bir kavramı ilk kez kullanırken bir cümleyle tanımla.
3. **Küçük adımlar.** Her adım derlenebilir ve test edilebilir olsun. Büyük "her şeyi birden" değişiklik yapma.
4. **Kanıt CLI çıktısıdır.** "Tamamlandı" demeden önce fazın doğrulama komutlarını çalıştır ve gerçek çıktıyı göster. Visual Studio'daki yeşil tik kanıt değildir.
5. **Dürüst rapor.** Fazın sonunda: PASS kriterlerinin her biri için ✅/❌ ve kanıtı; doğrulanmayan ya da yapılmayan her şeyi açıkça yaz ("race testi yazıldı ama CI'da henüz koşmadı" gibi). Yapılmayan işi yapılmış gibi gösterme.
6. **Karar değişikliği.** Bir ADR'ye aykırı davranman gerekiyorsa dur, gerekçeyi yaz, Salih'e sor. Onaylanırsa ADR'yi güncelle.
7. **Dil.** Açıklamalar, belgeler ve commit gövdeleri Türkçe. Kod, tanımlayıcılar, log mesajları, API alanları İngilizce.

## Token ekonomisi (Salih Pro paketinde; limit sınırlı)

**Okuma**
- Belgeleri bütünüyle okuma. `docs/08-roadmap.md`'den yalnızca **Durum** tablosunu ve **aktif fazın bölümünü** oku (başlığı `Grep` ile bul, sonra `offset`/`limit` ile o aralığı oku). Diğer belgelerden yalnızca işin ihtiyaç duyduğu bölümü oku.
- Aynı oturumda zaten okuduğun dosyayı yeniden okuma. Büyük dosyada önce `Grep`, sonra hedefli okuma.
- Komut çıktılarını kısalt: `dotnet build` için `-v q`; `dotnet test` (Microsoft.Testing.Platform, `--logger` yok) zaten özet basar; uzun çıktıyı `tail`/`Select-Object -Last` ile kes. Rapor için hata satırları ve özet yeter.

**Model ve effort**
- Varsayılan model Sonnet, effort `auto`/`medium`.
- Opus (veya `opusplan` + Plan Mode) yalnızca: F03 outbox/inbox çekirdeği, F05 atama/eşzamanlılık çekirdeği, race testi tasarımı ve faz sonu kritik review. Bu işlere gelince Salih'e model değiştirmeyi **öner**; kendin varsayma.
- `high` effort yalnız eşzamanlılık, transaction ve güvenlik kodunda.

**Alt ajanlar**
- **Kod yazan alt ajan açma.** Kod ana oturumda yazılır (alt ajan her seferinde bağlamı sıfırdan okur; çoklu ajan çok daha fazla token harcar).
- Alt ajan yalnızca **salt okuma** işleri için ve `model: haiku` ile: geniş kod araması, uzun log/çıktı özetleme. Sonucu ana oturum doğrular.
- Paralel ajan, "her açıdan inceleme" gibi çoklu ajan akışları yalnızca Salih açıkça isterse.

**Oturum düzeni**
- Her faz (F05 gibi büyük fazlarda her alt adım) **yeni oturumda** başlar. Faz/adım bitince Durum tablosunu güncelle; Salih'e `/clear` önerebilirsin.
- Oturum uzadıysa (bağlam yarıyı geçtiyse) yarım iş bırakmadan önce `docs/08-roadmap.md` Durum tablosuna ve PR açıklamasına kısa bir devir notu yaz: ne bitti, ne kaldı, sıradaki komut. Sonraki oturum bu notla devam eder.
- Açıklamalar kısa olsun: kavramı bir cümleyle tanımla, uzun ders anlatma. Salih ayrıntı isterse sorar.

## Mimari kurallar (ihlal = review'da red)

- **Veri sahipliği:** Bir servis başka bir servisin veritabanına ya da Redis anahtarlarına **asla** erişmez. İhtiyaç duyduğu veriyi olaylarla kendi tablosuna kopyalar veya sahibine API/gRPC ile sorar.
- **BuildingBlocks yalnız altyapı içerir** (outbox, inbox, telemetri, auth ayarları, ProblemDetails). Domain kodu, entity, iş kuralı paylaşılmaz.
- **Sözleşmeler sürümlüdür:** `OrderCreated.v1`. Yayınlanmış bir sözleşmeyi kırma; yeni sürüm aç.
- **Kültür (Türkçe I/ı tuzağı):** Servislerde `InvariantGlobalization` açık. Metin karşılaştırmada `StringComparison.Ordinal`/`OrdinalIgnoreCase`; kültüre bağlı `ToLower()`/`ToUpper()` yok (`ToLowerInvariant()`).
- **Zaman:** `DateTime.Now` / `DateTime.UtcNow` / SQL `now()` iş kurallarında yasak. `TimeProvider` enjekte edilir; DB sorgularına zaman parametre olarak geçer. Bütün zamanlar UTC (`timestamptz`).
- **Eşzamanlılık:** Durum geçişleri koşullu update veya optimistic concurrency (`xmin`) ile yapılır. "Önce oku, `if` ile kontrol et, sonra yaz" tek başına yeterli değildir.
- **Idempotency:** Dışarıdan gelen her yazma isteği ve her tüketilen mesaj tekrar gelebilir varsayılır.
- **Outbox:** Olay yayınlamak = iş verisiyle **aynı transaction'da** outbox tablosuna yazmak. Doğrudan RabbitMQ'ya publish yok.
- **Hata modeli:** Beklenen iş hataları exception değil, sonuç tipiyle döner; HTTP'de `ProblemDetails`. Exception yalnız beklenmeyen durumlar için.
- **API stili:** Minimal API + endpoint grupları. Controller yok.
- **Servis içi yapı:** `Domain/` (saf, dış bağımlılık yok) · `Features/<Özellik>/` (endpoint + handler + doğrulama) · `Infrastructure/` (EF, Redis, RabbitMQ). Gereksiz katman/arayüz ekleme; tek implementasyonu olan arayüz yalnız test ya da sınır gerektiriyorsa.

## Paket kuralları

- Sürümler yalnızca `Directory.Packages.props` içinde (Central Package Management).
- Yeni paket eklemeden önce: .NET 10 uyumluluğu, son kararlı sürüm ve **lisans** kontrol edilir; sonuç PR açıklamasına yazılır.
- **Kullanılmayacak paketler** (lisans veya karar gereği): MediatR, AutoMapper, MassTransit (v9 ticari), FluentAssertions (v8+ ticari). Assertion için Shouldly.
- Eski blog/StackOverflow örneklerini kontrol etmeden kullanma; .NET 10 / güncel paket API'sini doğrula.

## Test kuralları

- Salih test yazmayı öğreniyor: test yazmaya başlamadan önce hangi test türünü neden seçtiğini kısa bir tabloyla söyle (bkz. `docs/07-testing-and-performance.md`).
- Race condition, transaction ve kilit davranışı **mock ile test edilmez**; Testcontainers ile gerçek PostgreSQL kullanılır.
- Her test kendi verisini kurar (test başına benzersiz `merchant_id` / `courier_id`); testler sıra bağımsızdır.
- Flaky test kabul edilmez. Zaman için `FakeTimeProvider`, bekleme için `Task.Delay` yerine koşul bekleme.
- Bug bulunursa: önce onu yakalayan regression testi, sonra düzeltme.

## Komutlar

```bash
# Altyapıyı başlat (PostgreSQL, RabbitMQ, Redis, trace paneli)
docker compose -f deploy/docker-compose.yml up -d

# Build — uyarı = hata
dotnet build RotaApp.slnx -warnaserror

# Bütün testler (Docker çalışıyor olmalı: Testcontainers)
dotnet test RotaApp.slnx

# Tek test projesi
dotnet test tests/Rota.Orders.IntegrationTests

# EF migration (örnek: Orders)
dotnet ef migrations add <Ad> --project src/Services/Orders/Rota.Orders.Api
```

## Ortam notları (Windows + Visual Studio)

- Salih Visual Studio 2026 kullanıyor; dosyalar dışarıdan değişince VS yeniden yükler. `.slnx` ve `.csproj` değişikliklerinde VS projeyi yeniden yükler, bu normaldir.
- Git Bash'te `/` ile başlayan argümanlar Windows yoluna çevrilir: `MSYS_NO_PATHCONV=1 komut /yol`.
- Satır sonları `.gitattributes` ile yönetilir; CRLF/LF farkı yüzünden oluşan "değişiklikleri" commit'leme.
- Lokal geliştirme: altyapı Docker Compose'da, servisler VS "Multiple Startup Projects" ile (bkz. ADR-012).

## Git

- Her faz kendi branch'inde: `faz/f02-orders`. Faz bitince PR.
- Commit mesajı: Conventional Commits (`feat(orders): idempotent sipariş oluşturma`).
- Commit ve push yalnızca Salih isteyince.
- Test kimlik bilgileri yalnızca `deploy/` altındaki seed dosyalarında; gerçek sır (secret) repoya girmez.
