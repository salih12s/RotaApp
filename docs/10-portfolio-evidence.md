# 10 — Portföy kanıt planı

Amaç: Projeyi bitirdiğinde mülakatçıya "şu teknolojileri kullandım" değil, **"şu problemi şöyle çözdüm, şu testle kanıtladım, şu ölçümle karar verdim"** diyebilmek.

**Dürüstlük kuralı:** README'ye, CV'ye ve LinkedIn'e yalnızca ölçülmüş ve repoda kanıtı olan sayılar yazılır. Ölçülmemiş değer için `⟨ölçülecek⟩` yer tutucusu kalır.

## 1. GitHub README taslağı (İngilizce, sonunda Türkçe özet)

1. **Başlık + tek cümle** + CI rozeti + (F13 varsa) canlı harita GIF'i
2. **Problem** — 3 madde (yanlış atama, görünmezlik, kanıtsız teslimat)
3. **Architecture** — mermaid diyagramı ([03](03-architecture.md) §1) + iletişim matrisi özeti
4. **Key engineering decisions** — 6–8 madde, her biri ADR'ye link:
   - Çift atama nasıl imkânsız kılındı (koşullu update + kısmi unique indeks)
   - Neden outbox/inbox ve neden kendimiz yazdık
   - Neden gRPC streaming (konum) ve neden REST (dış API)
   - Redis neden doğruluk kaynağı değil
   - Neden Kafka / Saga / Aspire yok
5. **Reliability** — arıza senaryoları tablosu ([06](06-security-and-errors.md) §7) ve hangi testin kanıtladığı
6. **Performance** — F11 sonuç tablosu + bir önce/sonra optimizasyonu
7. **Observability** — tek siparişin trace ekran görüntüsü
8. **Testing** — test piramidi sayıları, race testleri, Testcontainers
9. **Run it** — tek komut, portlar, test kullanıcıları seed'den
10. **Limitations** — kuş uçuşu mesafe, mTLS yok, tek makinede ölçüm, simülatör
11. **Türkçe özet**

## 2. Faz başına toplanacak kanıtlar

| Faz | Kanıt | Yer |
|---|---|---|
| F00 | CI yeşil ekran görüntüsü | `docs/evidence/f00/` |
| F02 | R-5 test çıktısı (10 eşzamanlı istek → 1 sipariş) | PR açıklaması |
| F03 | RabbitMQ kesintisi öncesi/sonrası sayılar; ilk servisler arası trace | `docs/evidence/f03/` |
| F04 | Konum alımı taban çizgisi | `perf/reports/` |
| F05 | R-1 race testi çıktısı (20 tur, çift atama 0); Redis kapalı testi | `docs/evidence/f05/` |
| F07 | Yavaş işletme izolasyonu ölçümü | `docs/evidence/f07/` |
| F08 | Rol × uç yetki matrisi test raporu | `docs/evidence/f08/` |
| F09 | N+1 sorgu sayısı önce/sonra (DataLoader'sız vs. DataLoader'lı) | `docs/evidence/f09/` |
| F10 | Uçtan uca tek trace ekran görüntüsü | `docs/evidence/f10/` |
| F11 | Load/stress/spike/soak raporları; optimizasyon önce/sonra | `perf/reports/` |
| F12 | Temiz makinede `docker compose up` süresi | README |
| F13 | Canlı harita GIF'i | README |

## 3. Diyagramlar

- Sistem görünümü (C4 seviye 2 benzeri) — mermaid
- Atama sekans diyagramı — mermaid
- Sipariş ve kurye durum makineleri — mermaid
- Olay akışı (kim neyi yayınlar/tüketir) — tablo + mermaid

Hepsi `docs/` içinde mermaid olarak; GitHub doğrudan çizer, ayrı görsel dosyası gerekmez.

## 4. CV maddesi şablonları (sayılar ölçüldükten sonra doldurulur)

- Designed and built a microservice-based last-mile delivery platform in **.NET 10** (5 services, PostgreSQL, RabbitMQ, Redis, gRPC, GraphQL, YARP) with **zero double-assignments** verified by concurrent integration tests (`⟨N⟩` parallel orders × 20 rounds).
- Implemented **transactional outbox/inbox** on raw RabbitMQ.Client; demonstrated **no event loss** across a broker outage with `⟨N⟩` in-flight orders.
- Built a **gRPC streaming** location pipeline (Redis GEO + PostgreSQL `COPY` into daily partitions) sustaining `⟨X⟩` points/s at p95 `⟨Y⟩` ms; chose `COPY` over EF Core bulk insert after measuring `⟨Z⟩×` throughput.
- Delivered **signed webhooks** with exponential backoff, DLQ/replay and per-tenant bulkheads; a slow tenant did not affect others' p95 (`⟨ms⟩`).
- Exposed an operations **GraphQL API (Hot Chocolate)** with DataLoaders (N+1 eliminated: `⟨a⟩` → `⟨b⟩` queries) and Redis-backed subscriptions across instances.
- End-to-end **OpenTelemetry** tracing across HTTP, gRPC and RabbitMQ; CI with Testcontainers; container images on GHCR.

## 5. Mülakat hikâyeleri (STAR: Durum → Görev → Eylem → Sonuç)

| # | Soru | Hikâye | Kanıt |
|---|---|---|---|
| 1 | "Eşzamanlılık problemini nasıl çözdün?" | Aynı kuryeye iki teklif → koşullu update + kısmi unique indeks; neden dağıtık kilit değil | R-1, ADR-002 |
| 2 | "Mesaj kaybını nasıl önledin?" | Outbox/inbox; RabbitMQ kapalıyken 50 sipariş | R-7, ADR-010 |
| 3 | "Aynı mesaj iki kez gelirse?" | Inbox + idempotency anahtarı + webhook `id` | R-5, R-6 |
| 4 | "gRPC'yi neden ve nerede kullandın?" | Konum akışı (client-stream), teklif itme (server-stream), yakındaki kurye (unary + deadline); neden dış API'de değil | 03 §3 |
| 5 | "Redis çökerse ne olur?" | Türetilmiş indeks, doğruluk PostgreSQL'de; testle kanıt | F05 PASS 6, ADR-001 |
| 6 | "Performans kararını nasıl verdin?" | EF vs `COPY` ölçümü, toplu yazma boyutu | F11 raporu |
| 7 | "GraphQL'de N+1?" | DataLoader + toplu gRPC; sorgu sayısı testi | R-12 |
| 8 | "Neyi kullanmadın ve neden?" | Kafka, Saga, Aspire, MassTransit, Fusion — her birinin gerekçesi | ADR-010/012/013/015 |
| 9 | "Güvenlik?" | 3 kimlik tipi, sahiplikte 404, SSRF, HMAC, KVKK saklama | 06 |
| 10 | "Ölçeklendirme?" | Durumsuz servisler; durumlu gRPC akışları için Redis Pub/Sub yönlendirmesi; `SKIP LOCKED` ile çok instance'lı işçiler | F05, F09 |

## 6. "Yapmadıklarım ve neden" (README'de açıkça)

- Gerçek yol rotası yok → kapsam; arayüz hazır (ADR-008).
- mTLS yok → iç ağ izolasyonu + token; üretimde eklenmeli.
- Kafka yok → ihtiyaç ölçülmedi (ADR-013).
- Saga yok → telafi gerektiren çok adımlı para/stok akışı yok.
- Ölçümler tek makinede → göreli karşılaştırma için anlamlı, mutlak kapasite iddiası değil.
