# Tracking: konum akışı taban çizgisi (F04)

> **Bu bir hedef değil, başlangıç çizgisi.** F04 "hedef yok, yalnız taban çizgisi" der (08-roadmap). Optimizasyon kararı F11'de, ölçüme dayanarak verilir. Aşağıdaki sayılar tek bir makinede, istemci ve sunucu birlikte çalışırken alındı.

## 1. Ortam

| | |
|---|---|
| Makine | Dizüstü: Intel Core i7-13620H (10 çekirdek, 16 mantıksal), 31,7 GB RAM, Windows 11 |
| Tracking | Release derlemesi, gerçek süreç (`dotnet run -c Release`), `Logging:Default=Warning` |
| PostgreSQL | `postgres:18.6-alpine`, **geçici** Docker konteyneri (compose'taki değil: yerel Windows PostgreSQL 5432'yi tutuyor, bkz. F02 raporu) |
| Redis | `redis:8.10.3-alpine`, geçici Docker konteyneri |
| İstemci | `Rota.Sim.CourierFleet` (Release), **aynı makinede** |
| Commit | `faz/f04-tracking` (commit edilmemiş çalışma ağacı) |

Hepsi aynı CPU'yu paylaşıyor: simülatör, Tracking, PostgreSQL, Redis ve Docker. Sayılar bir üretim ortamının değil, bu makinenin sayılarıdır.

## 2. Senaryolar

Her koşuda aynı servis süreci kullanıldı (art arda). Konum sorgusu (`GetNearbyCouriers`, merkez Beyoğlu, 5 km, 20 kurye) simülatörün içinden saniyede 10 kez, konum akışıyla **aynı anda** soruldu.

| # | Komut | Hedef yük |
|---|---|---|
| A | `--couriers 200 --interval 3 --duration 60 --probe-nearby --disconnect-rate 0.01 --reorder-rate 0.05` | ~67 nokta/sn (roadmap'teki gerçekçi yük, kopma ve sırasız gönderimle) |
| B | `--couriers 2000 --interval 1 --duration 30 --probe-nearby` | 2.000 nokta/sn |
| C | `--couriers 1000 --interval 0.2 --duration 30 --probe-nearby` | 5.000 nokta/sn |
| D | `--couriers 3000 --interval 0.3 --duration 30 --probe-nearby` | 10.000 nokta/sn |

## 3. Sonuç

| # | Ulaşılan nokta/sn | Gönderilen | Veritabanına yazılan | Akış hatası | `GetNearbyCouriers` p50 / p95 / p99 / max (ms) |
|---|---|---|---|---|---|
| A | **65,8** | 3.950 | 3.950 | 0 | 3,9 / 7,5 / 15,5 / 703,9 |
| B | **1.975** | 59.366 | 59.366 | 0 | 4,7 / 13,3 / 46,3 / 110,5 |
| C | **4.720** | 142.050 | 142.050 | 0 | 5,0 / 27,1 / 58,6 / 443,4 |
| D | **9.607** | 288.719 | 288.719 | 0 | 10,3 / 84,0 / 156,5 / 355,1 |

- **Veri kaybı yok:** her koşuda `location_points`'e yazılan satır sayısı gönderilen nokta sayısına birebir eşit. Servis logunda tek uyarı yok (`history_dropped` sayacı tetiklenseydi "Dropped N history points" hatası görünürdü).
- **A'da sırasız gönderim:** gönderilen 3.950 noktanın 182'si (%4,6) sunucuda "daha yeni değil" sayıldı. Simülatör %5 oranında sırasız gönderecek şekilde ayarlıydı; hem anlık konumu geri almadı hem geçmişe yazıldı (3.950 satır). Çevrimdışı kalan kuryeler (42 olay) biriken noktalarını bağlantı dönünce gönderdi.
- **B ve C'de hata 0:** sırasız gönderim ve kopma kapalıydı, yalnız yük ölçüldü.
- **Tutarlılık kontrolü (A sonrası):** `location_points` 3.950 satır, 200 farklı kurye, `location_points_staging` boş, Redis `trk:lastseen` içinde 200 kurye.

## 4. Darboğaz analizi

**Yapılmadı.** Bu turda trace/metrik ekran görüntüsü ya da CPU profili alınmadı; elimizde yalnız dışarıdan ölçülen sonuçlar var. Gözlemler:

- Tavan bulunamadı: D'de bile (≈9,6 bin nokta/sn) hata ve veri kaybı yok.
- C'de hedefin (%94,4) altında kalınması büyük olasılıkla **istemci tarafı** (3.000'e yakın görev ve zamanlayıcı aynı makinede), ama bu doğrulanmadı.
- Sorgu gecikmesi yükle büyüyor: p95 7,5 ms (A) → 84 ms (D). Aynı makinede yoğun yazma ile aynı CPU'yu paylaşmanın etkisi olabilir; ayrıştırılmadı.
- A'daki 703,9 ms'lik max, koşunun başındaki ilk çağrı (JIT ısınması, bağlantı kurulumu) olabilir; doğrulanmadı.

## 5. Karar ve gerekçe

**Optimizasyon yapılmadı ve önerilmiyor.** Gerçekçi yük (200 kurye, saniyede ~67 nokta) için sunucu bu makinede yaklaşık **145 kat** pay bırakıyor (9.607 / 66). Ölçüm olmadan optimize etmek kuralı gereği F11'e bırakıldı: orada EF `AddRange` ile `COPY` karşılaştırması, yazıcı parametrelerinin (batch boyutu, bekleme) etkisi ve gerçek darboğaz analizi yapılacak.

## 6. Bilinen sınırlar

- **Tek makine:** istemci, sunucu, PostgreSQL ve Redis aynı CPU'yu paylaştı; ağ gecikmesi yok.
- **Süre kısa:** 30-60 sn. Uzun süreli davranış (bellek sızıntısı, bölüm büyümesi, Redis bellek) ölçülmedi (soak testi F11 kapsamında).
- **Tek örnek:** birden çok Tracking örneğinin aynı veritabanına yazması ölçülmedi.
- **Veritabanı kısa bir süre boş başladı:** bölümler yeni oluşturulmuş, tablolar küçük. Büyük tabloda `COPY` ve `ON CONFLICT` maliyeti ölçülmedi.
- **Sorgu yükü sabit:** saniyede 10 çağrı, tek merkez; gerçek Dispatch yükü farklı olacak (F05).
- **Tek deneme:** her senaryo bir kez koşuldu; tekrarlanabilirlik (varyans) ölçülmedi. Simülatörde `--seed` var (aynı tohum aynı kurye kimlikleri ve rotaları verir); B-D'de her koşu farklı bir kurye kümesi olsun diye rastgele tohum kullanıldı, yani koşular birebir tekrarlanamaz.
