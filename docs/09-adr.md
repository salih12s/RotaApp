# 09 — Mimari karar kayıtları (ADR)

Her karar: **Bağlam** (problem) · **Karar** · **Sonuç** (kazanç ve bedel) · **Alternatifler**. Durum: `Kabul` / `Öneri` (Salih onayı bekliyor) / `Değiştirildi`.

---

### ADR-001 — Kurye durumunun doğruluk kaynağı PostgreSQL; Redis yalnızca indeks
**Durum:** Kabul
**Bağlam:** Yakındaki kuryeyi bulmak için Redis GEO hızlı. Ama Redis kalıcılığı ve transaction'ı zayıf; kurye durumu orada tutulursa Redis kaybında çift atama riski doğar.
**Karar:** Kurye durumu (`Available`, `Offered`...) ve teklifler Dispatch'in PostgreSQL'inde. Redis GEO yalnızca "kim yakında" sorusu için türetilmiş indeks.
**Sonuç:** Redis silinse de doğruluk korunur, indeks 3 sn'de kendini doldurur. Bedel: atamada bir DB yazması (koşullu update) gerekir.
**Alternatifler:** Durumu Redis'te Lua script ile atomik tutmak — daha hızlı ama kalıcılık/denetim izi zayıf.

### ADR-002 — Kurye rezervasyonu koşullu update ile; sipariş geçişleri optimistic concurrency (`xmin`) ile
**Durum:** Kabul
**Bağlam:** Aynı kuryeye iki sipariş aynı anda teklif vermeye çalışabilir; aynı siparişe iptal ve teslim alma aynı anda gelebilir.
**Karar:** Kurye: `UPDATE ... WHERE id=@id AND state='Available'`, etkilenen satır sayısına bakılır. Sipariş: aggregate yüklenir, değiştirilir, `xmin` ile kaydedilir; çakışma 409.
**Sonuç:** Yarış veritabanında çözülür; uygulama kilidi ya da dağıtık kilit gerekmez.
**Alternatifler:** `SELECT ... FOR UPDATE` (kötümser kilit) — doğru ama kilit bekleme süresi yük altında gecikme üretir; Redis dağıtık kilit — ek arıza noktası, gereksiz.

### ADR-003 — Zamanlanmış işler DB'de `FOR UPDATE SKIP LOCKED` ile
**Durum:** Kabul
**Bağlam:** Teklif zaman aşımı, yeniden arama, webhook retry, outbox relay — hepsi "zamanı gelen satırı işle". Birden çok instance aynı satırı iki kez işlememeli.
**Karar:** Hepsi tek kalıp: zamanı gelen satırlar `SKIP LOCKED` ile alınıp aynı transaction'da işlenir.
**Sonuç:** Ek altyapı yok, transaction güvencesi var, test edilebilir. Bedel: periyodik sorgu yükü (kısmi indekslerle küçük tutulur; aralık F11'de ölçülür).
**Alternatifler:** RabbitMQ delayed message eklentisi (ek eklenti, iptal edilen teklifi kuyruktan silmek zor); Hangfire/Quartz (ek bağımlılık, bu ihtiyaç için ağır).

### ADR-004 — Webhook'larda sıra garantisi yok; `orderVersion` + en az bir kez teslim
**Durum:** Kabul
**Bağlam:** Dağıtık sistemde sıralı teslim, paralelliği öldürür ve tek bir yavaş mesaj bütün akışı durdurur.
**Karar:** Webhook'lar sırasız ve en az bir kez gönderilir; her olay `id` ve `orderVersion` taşır; alıcı eski sürümü ve tekrarı ayıklar.
**Sonuç:** Basit, ölçeklenebilir, dürüst sözleşme. Bedel: işletme tarafına küçük bir sorumluluk (README'de belgelenir).

### ADR-005 — Kimlik sunucusu olarak Keycloak
**Durum:** Öneri (açık karar K4)
**Bağlam:** Üç farklı kimlik tipi (makine, kullanıcı, rol) gerekiyor. Kendi token sunucusunu yazmak güvenlik riski ve öğrenme hedefi dışı.
**Karar:** Keycloak container'ı; realm/roller/test kullanıcıları seed dosyasıyla.
**Sonuç:** Gerçek OIDC akışları (client credentials, kullanıcı girişi) öğrenilir. Bedel: ek container, ilk kurulum zahmeti.
**Alternatifler:** Kendi küçük JWT issuer'ımız (basit ama gerçekçi değil); Duende IdentityServer (ticari lisans).

### ADR-006 — F08'e kadar yalnız Development/test için `DevAuth` şeması
**Durum:** Kabul
**Bağlam:** Yetki kuralları ilk fazdan yazılmalı ama Keycloak F08'de geliyor.
**Karar:** Politikalar baştan gerçek; claim'ler Development'ta başlıktan okunur. Development dışı ortamda `DevAuth` kaydı başlangıçta hata verir.
**Sonuç:** Sahiplik/rol testleri F02'den itibaren var. Bedel: F08'de geçiş işi (küçük).

### ADR-007 — Takip token'ı Orders tarafından asimetrik anahtarla imzalanır
**Durum:** Kabul
**Bağlam:** Müşteri hesap açmadan tek bir siparişi izlemeli; Ops API token'ı doğrulamalı ama üretememeli.
**Karar:** ES256 JWT; Orders özel anahtarla imzalar, Ops API JWKS ucundan açık anahtarla doğrular.
**Sonuç:** Ortak sır yok; token'ın kapsamı ve süresi sınırlı.

### ADR-008 — Mesafe kuş uçuşu (haversine), ETA sabit hızla
**Durum:** Öneri (açık karar K1)
**Bağlam:** Gerçek yol rotası (OSRM vb.) doğruluğu artırır ama kapsamı ve altyapıyı büyütür.
**Karar:** Aday sıralaması ve ETA kuş uçuşu mesafeyle; ortalama hız yapılandırılabilir.
**Sonuç:** Basit ve test edilebilir. Bedel: köprü/nehir gibi durumlarda yanlış en yakın kurye. README'de sınır olarak belirtilir; gelecekte `IRouteEstimator` arayüzüyle değiştirilebilir.

### ADR-009 — Servis içinde dikey dilim (feature folder) + saf Domain klasörü; katman başına proje yok
**Durum:** Kabul
**Bağlam:** Her servis küçük; 4 projeli Clean Architecture dosya ve arayüz sayısını fayda getirmeden artırır.
**Karar:** Servis başına tek proje: `Domain/`, `Features/`, `Infrastructure/`. Domain'in EF/ASP.NET'e bağımlı olmaması mimari testle (F01) zorlanır.
**Sonuç:** Az tören, okunabilir kod; domain yine izole ve test edilebilir.
**Alternatifler:** Klasik 4 katman (Domain/Application/Infrastructure/Api projeleri).

### ADR-010 — MediatR, AutoMapper, MassTransit, FluentAssertions kullanılmaz
**Durum:** Kabul
**Bağlam:** Bu kütüphaneler ticari lisansa geçti (MassTransit v9, MediatR, AutoMapper, FluentAssertions v8). Ayrıca öğrenme hedefi mekanizmaları içinden görmek.
**Karar:** Handler'lar düz sınıf; eşleme elle; mesajlaşma `RabbitMQ.Client` üzerinde kendi outbox/inbox'ımız; assertion Shouldly.
**Sonuç:** Lisans riski yok; outbox/inbox mülakatta içinden anlatılabilir. Bedel: daha fazla altyapı kodu (BuildingBlocks'ta, testli).

### ADR-011 — Orders tek bir `OrderStatusChanged.v1` olayı yayınlar
**Durum:** Kabul
**Bağlam:** Tüketicilerin hepsi "durum değişti → yeni durum" ile aynı işi yapıyor.
**Karar:** `OrderCreated.v1` (zengin veri) + diğer bütün geçişler için `OrderStatusChanged.v1` (`fromStatus`, `toStatus`, `orderVersion`).
**Sonuç:** Az sözleşme, kolay tüketim. Bedel: olay adı tek başına "ne oldu"yu söylemez; `toStatus` okunur.

### ADR-012 — Lokal geliştirme: Docker Compose (altyapı) + Visual Studio Multiple Startup (servisler)
**Durum:** Öneri (açık karar K6)
**Bağlam:** Salih Visual Studio 2026 kullanıyor. .NET Aspire konforlu ama bağlantı kurulumunu gizler; deploy da ayrı tanım ister.
**Karar:** Altyapı `deploy/docker-compose.yml`; servisler VS'ten F5 ile (paylaşılan `RotaApp.slnLaunch`). Trace paneli olarak yalnızca Aspire Dashboard imajı.
**Sonuç:** Her port/bağlantı açıkça görülür; deploy aynı compose bilgisiyle yapılır; servisler arası breakpoint ile debug.
**Alternatifler:** .NET Aspire AppHost.

### ADR-013 — Kafka yok (koşullu opsiyonel faz)
**Durum:** Kabul
**Bağlam:** İlanlarda Kafka geçiyor; ama projede olay hacmi düşük ve yeniden oynatma ihtiyacı yok.
**Karar:** RabbitMQ yeterli. Kafka yalnızca "okuma modelini/ETA'yı geçmişten yeniden kurma" ihtiyacı ölçümle doğarsa F14'te.
**Sonuç:** Gereksiz altyapı yok; mülakatta "neden kullanmadım" savunulabilir.

### ADR-014 — Konum yayını Redis Pub/Sub ile, RabbitMQ ile değil
**Durum:** Kabul
**Bağlam:** Konum saniyede binlerce kez değişir; eski konumun değeri yoktur.
**Karar:** Anlık konum yayını Redis Pub/Sub (kalıcı değil, en iyi çaba); konum geçmişi doğrudan Tracking DB'sine.
**Sonuç:** RabbitMQ yalnızca kaybolmaması gereken iş olaylarını taşır. Bedel: Pub/Sub'da kaçan mesaj telafi edilmez (bir sonraki konumla düzelir).

### ADR-015 — GraphQL tek bir Ops API'de (BFF), Hot Chocolate Fusion yok
**Durum:** Kabul
**Bağlam:** Panel üç servisin verisini birlikte istiyor. Fusion (dağıtık şema birleştirme) güçlü ama karmaşık ve sürüm riski taşıyor.
**Karar:** Ops API olaylardan okuma modeli kurar, GraphQL sunar; canlı veriyi Redis Pub/Sub ve Tracking gRPC'den alır.
**Sonuç:** Tek şema, basit yetki, CQRS okuma tarafı örneği. Bedel: okuma modeli nihai tutarlı.

### ADR-016 — Webhook gönderim zamanlaması DB'de, RabbitMQ retry kuyruğunda değil
**Durum:** Kabul
**Bağlam:** Webhook retry'ları dakikalar sürer, işletme bazında görünür ve elle yeniden gönderilebilir olmalı.
**Karar:** `webhook_deliveries` tablosu + `next_attempt_at` + `SKIP LOCKED` (ADR-003 kalıbı).
**Sonuç:** Her denemenin geçmişi sorgulanabilir; DLQ listesi ve replay basit bir sorgu.
