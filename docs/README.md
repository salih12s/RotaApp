# Rota — Belge haritası

> **Rota**, işletmelerin siparişlerini konum bazlı olarak en uygun kuryeye otomatik atayan, kurye konumunu gerçek zamanlı işleyen ve teslimat durumunu güvenilir şekilde bildiren, mikroservis tabanlı bir son-mil teslimat platformudur.

## Okuma sırası

| # | Belge | İçerik |
|---|---|---|
| 01 | [PRD](01-prd.md) | Amaç, kullanıcılar, kapsam, kullanıcı hikâyeleri, fonksiyonel ve fonksiyonel olmayan gereksinimler |
| 02 | [Domain](02-domain.md) | Bounded context'ler, ortak dil (sözlük), durum makineleri, değişmez kurallar |
| 03 | [Mimari](03-architecture.md) | Servisler, veri sahipliği, iletişim matrisi, akış diyagramları, solution yapısı, lokal geliştirme |
| 04 | [Veri modeli](04-data-model.md) | Servis başına PostgreSQL tabloları, indeksler, Redis anahtarları |
| 05 | [Sözleşmeler](05-contracts.md) | REST uçları, gRPC proto taslakları, olaylar, webhook, GraphQL şeması |
| 06 | [Güvenlik ve hata yönetimi](06-security-and-errors.md) | Kimlik doğrulama, yetki politikaları, hata kataloğu, dayanıklılık, arıza senaryoları |
| 07 | [Test ve performans](07-testing-and-performance.md) | Test türleri, test projeleri, ölçüm hedefleri, k6 senaryoları |
| 08 | [Yol haritası](08-roadmap.md) | Fazlar: GOAL, kapsam, PASS/FAIL, doğrulama komutları, durum tablosu |
| 09 | [ADR](09-adr.md) | Mimari kararlar ve gerekçeleri |
| 10 | [Portföy kanıt planı](10-portfolio-evidence.md) | README, diyagramlar, ölçüm kanıtları, CV maddeleri, mülakat hikâyeleri |

Ajanlar için çalışma kuralları: repo kökündeki [CLAUDE.md](../CLAUDE.md).

## Açık kararlar

Aşağıdakiler henüz Salih tarafından kesinleştirilmedi. Belgeler **varsayılan** seçenekle yazıldı; değişirse ilgili ADR ve belgeler güncellenir.

| # | Soru | Varsayılan | Etkilediği yer |
|---|---|---|---|
| K1 | Mesafe/ETA: kuş uçuşu mu, gerçek yol rotası mı? | Kuş uçuşu (haversine) + sabit ortalama hız | ADR-008, F05 |
| K2 | Kurye kapasitesi | Bir kurye aynı anda tek sipariş | Domain, F05 |
| K3 | Harita arayüzü (React) | Opsiyonel faz F13 | 08-roadmap |
| K4 | Kimlik sunucusu | Keycloak | ADR-005, F08 |
| K5 | Dağıtım hedefi | GHCR'a image + `docker compose` ile tek komut çalıştırma; VPS varsa ek adım | F12 |
| K6 | Lokal orkestrasyon | Docker Compose (altyapı) + VS Multiple Startup (servisler); Aspire yok | ADR-012 |
| K7 | Belgelerin dili | `docs/` Türkçe; GitHub README İngilizce + Türkçe özet | 10-portfolio-evidence |

## Belge kuralları

- Belge ile kod çelişirse belge kazanır; çelişki bulunca belge ya da kod bilinçli olarak güncellenir.
- Bir karar değişirse önce [09-adr.md](09-adr.md) güncellenir, sonra kod.
- Sayısal hedefler (p95 vb.) ilk ölçümden sonra değişebilir; değişiklik gerekçesiyle birlikte [07](07-testing-and-performance.md)'ye yazılır. Hedefi sessizce gevşetmek yasak.
