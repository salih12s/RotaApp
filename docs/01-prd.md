# 01 — Ürün Gereksinimleri (PRD)

## 1. Problem

Kendi kurye filosuyla çalışan işletmeler (restoran zincirleri, eczaneler, market şubeleri, yerel kurye firmaları) siparişleri kuryelere çoğunlukla telefonla veya mesajlaşma gruplarıyla dağıtıyor. Bunun sonuçları:

- **Yanlış atama:** En yakın boş kurye bilinmiyor; uzak kurye gidiyor, sipariş gecikiyor.
- **Çift atama / unutulan sipariş:** Aynı siparişe iki kurye gidiyor ya da sipariş kimseye verilmiyor.
- **Görünmezlik:** Müşteri "siparişim nerede?" diye arıyor; işletme de bilmiyor.
- **Kanıtsız teslimat:** "Teslim ettim" diyen kuryenin gerçekten teslim edip etmediği kanıtlanamıyor.
- **Kopuk sistemler:** İşletmenin kendi sipariş sistemi teslimat durumundan haberdar olmuyor.

## 2. Çözüm

Rota:
1. İşletmenin sisteminden siparişi API ile alır.
2. Sipariş noktasına en yakın, uygun durumdaki kuryeye otomatik **teklif** gönderir.
3. Kurye süre içinde kabul etmezse teklifi sıradaki adaya geçirir; kimse bulunamazsa dispatcher'ı uyarır.
4. Kurye konumunu gerçek zamanlı işler; müşteriye canlı takip linki verir.
5. Teslimatı müşterinin verdiği tek kullanımlık kodla kanıtlar.
6. Her durum değişikliğini işletmenin sistemine imzalı webhook ile güvenilir şekilde bildirir.

## 3. Kullanıcılar (persona)

| Persona | Kim? | Rota'yı nasıl kullanır? |
|---|---|---|
| **İşletme (Merchant)** | Restoran zincirinin sipariş sistemi (makine istemcisi) | REST API ile sipariş oluşturur/iptal eder, webhook alır |
| **Kurye (Courier)** | Sahada motorlu kurye; mobil uygulama (projede simülatör) | Vardiya açar, konum akıtır, teklif kabul/ret eder, teslim alır ve teslim eder |
| **Dispatcher** | İşletmenin operasyon sorumlusu | Panelden siparişleri ve kuryeleri izler, atanamayan siparişi elle atar |
| **Son müşteri (Customer)** | Siparişi bekleyen kişi | Hesap açmadan, imzalı link ile kuryeyi canlı izler; teslim kodunu kuryeye söyler |

## 4. Kapsam

### MVP'de var
- Sipariş oluşturma (idempotent), sorgulama, iptal
- Otomatik atama: teklif → kabul / ret / zaman aşımı → sıradaki aday → atanamadı
- Dispatcher'ın elle ataması
- Kurye vardiyası (açık/kapalı) ve konum akışı
- Teslim alma, teslim kodu ile teslim, teslim edilemedi
- Müşteri için canlı takip
- İşletmeye webhook: imza, retry, DLQ, elle yeniden gönderim
- Dispatcher paneli için GraphQL API (arayüz opsiyonel)

### Bilinçli olarak kapsam dışı
- Gerçek yol rotası, trafik verisi (kuş uçuşu + sabit hız; bkz. ADR-008)
- Bir kuryenin aynı anda birden fazla sipariş taşıması (batching)
- Ödeme, tahsilat, kurye hakedişi
- Gerçek mobil uygulama ve SMS gönderimi (simülatör + log)
- Çoklu dil, çoklu ülke; yalnızca Türkiye, `Europe/Istanbul` gösterim saati, UTC saklama

## 5. Kullanıcı hikâyeleri ve kabul kriterleri

| ID | Hikâye | Kabul kriterleri |
|---|---|---|
| US-01 | İşletme olarak sipariş oluşturmak istiyorum ki teslimat başlasın. | Geçerli istek → 201 + sipariş + takip linki + teslim kodu. Aynı `Idempotency-Key` ile tekrar → aynı yanıt, ikinci sipariş yok. Aynı anahtar farklı içerik → 409. Hizmet alanı dışı → 422. |
| US-02 | İşletme olarak siparişimi iptal etmek istiyorum. | `PickedUp` öncesi iptal → 200, atanmış kurye serbest kalır. `PickedUp` ve sonrası → 409. Başka işletmenin siparişi → 404. |
| US-03 | Kurye olarak vardiya açıp kapatmak istiyorum ki bana iş gelsin/gelmesin. | Vardiya açık + konum taze → aday olabilir. Teslimattayken vardiya kapatılamaz → 409. |
| US-04 | Kurye olarak bana gelen teklifi anında görmek istiyorum. | Teklif oluşunca kuryenin açık gRPC akışına ≤ 1 sn içinde düşer. Akış kopuksa yeniden bağlanınca bekleyen teklif gönderilir. |
| US-05 | Kurye olarak teklifi kabul/ret etmek istiyorum. | Süresi içinde kabul → sipariş `Assigned`. Süresi dolmuş teklif → 409. Başkasının teklifi → 404. Ret → sıradaki adaya geçilir. |
| US-06 | Sistem olarak kimse kabul etmezse dispatcher'ı uyarmak istiyorum. | Yapılandırılan deneme sayısı / süre dolunca sipariş `Unassigned`, panelde görünür, işletmeye webhook gider. |
| US-07 | Dispatcher olarak siparişi elle atamak istiyorum. | Kurye `Available` ise atanır; değilse 409. Yalnız `dispatcher` rolü. |
| US-08 | Kurye olarak teslim aldığımı ve teslim ettiğimi bildirmek istiyorum. | Yalnız atanmış kurye. Doğru kod → `Delivered`. Yanlış kod → 400, 5 yanlıştan sonra kilit (409), dispatcher kilidi açabilir. |
| US-09 | Müşteri olarak kuryeyi canlı izlemek istiyorum. | İmzalı link ile, yalnız o sipariş için, yalnız `Assigned`–`PickedUp` arasında konum görünür. Süresi dolmuş token → reddedilir. |
| US-10 | İşletme olarak her durum değişikliğinden haberdar olmak istiyorum. | Her olay imzalı webhook ile gider. Alıcı çökükse retry; N denemeden sonra DLQ; işletme DLQ'yu listeleyip yeniden gönderebilir. |
| US-11 | Dispatcher olarak bütün siparişleri ve kuryeleri tek ekranda görmek istiyorum. | GraphQL ile filtreleme, sıralama, cursor pagination; canlı kurye konumları subscription ile. |

## 6. Fonksiyonel kurallar (varsayılan değerler, yapılandırılabilir)

| Kural | Varsayılan |
|---|---|
| Teklif süresi | 30 sn |
| Arama yarıçapı | 5 km |
| Sipariş başına en fazla teklif denemesi | 5 |
| Aday bulunamazsa yeniden deneme | 20 sn arayla, toplam 3 dk; sonra `Unassigned` |
| Kurye konum gönderme aralığı | 3 sn |
| Konum "taze" sayılma süresi | 30 sn (daha eskiyse aday olamaz) |
| Alış–teslim arası en uzun mesafe | 15 km (aşarsa 422) |
| ETA hızı | 20 km/sa ortalama (kuş uçuşu mesafeye göre) |
| Teslim kodu | 4 hane, 5 yanlış denemede kilit |
| Takip token ömrü | Sipariş oluşturulduktan sonra 6 saat |
| Webhook retry | 1 sn, 5 sn, 30 sn, 2 dk, 10 dk, 30 dk → sonra DLQ |
| Konum geçmişi saklama (KVKK) | 30 gün |

## 7. Fonksiyonel olmayan gereksinimler

| Alan | Gereksinim | Nasıl doğrulanır? |
|---|---|---|
| Doğruluk | Çift atama **0**; teslim edilmiş sipariş iptal edilemez | Race testleri (F05, F06) |
| Güvenilirlik | Altyapı kesintisinde olay kaybı **0** (outbox + inbox) | Kaos testi: RabbitMQ durdur/başlat (F03) |
| Performans | Konum alımı 5.000 nokta/sn'de p95 < 50 ms; sipariş oluşturma 200 istek/sn'de p95 < 100 ms; sipariş → ilk teklif p95 < 1 sn | k6 + simülatör (F11) |
| Ölçeklenebilirlik | Servisler durumsuz (stateless); her biri 2 instance ile doğru çalışır | 2 instance ile testler (F05, F08, F11) |
| Güvenlik | Her uç yetkili; kaynak sahipliği kontrolü; webhook imzası; kişisel veri maskeleme | API testleri (F08), log incelemesi |
| Gözlemlenebilirlik | Tek siparişin bütün yolculuğu tek trace'te; iş metrikleri; health check | Trace ekran görüntüsü (F10) |
| Taşınabilirlik | Temiz makinede tek komutla bütün sistem ayağa kalkar | `docker compose up` (F12) |
| Kalite | Uyarısız build, CI'da bütün testler yeşil | GitHub Actions |

## 8. Başarı ölçütleri (demo/ölçüm ortamında)

- Simüle edilen 500 kurye + dakikada 300 sipariş altında: çift atama 0, olay kaybı 0.
- Sipariş → kurye kabulü arası medyan süre ve p95'i raporlanmış.
- Webhook alıcısı 5 dk kapalı kaldığında, açılınca bütün olaylar teslim edilmiş.

## 9. Varsayımlar ve riskler

- Kurye ve işletme davranışı simülatörle üretilir; gerçek dünya gürültüsü (GPS sapması vb.) basitleştirilir.
- Ölçümler geliştirici dizüstü bilgisayarında yapılır; mutlak değil göreli karşılaştırma için anlamlıdır.
- En büyük kapsam riski: harita/ETA ve simülatörün büyümesi (bkz. [08-roadmap.md](08-roadmap.md) "Riskler").
