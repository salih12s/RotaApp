# Rota

A microservice-based last-mile delivery platform: assigns orders to the nearest suitable courier, processes courier locations in real time and reports delivery status reliably.

> 🚧 **Work in progress.** Phase F00 (skeleton, infrastructure, CI). See [docs/08-roadmap.md](docs/08-roadmap.md).

**TR:** Siparişleri konuma göre en uygun kuryeye otomatik atayan, mikroservis tabanlı bir son-mil teslimat platformu. Belgeler: [docs/](docs/README.md).

## Run locally

Requirements: .NET SDK 10.0.401+, Docker.

```bash
# Infrastructure: PostgreSQL, RabbitMQ, Redis, trace dashboard
docker compose -f deploy/docker-compose.yml up -d

# Build and test
dotnet build RotaApp.slnx -warnaserror
dotnet test RotaApp.slnx
```

Services run from Visual Studio with the **"Tüm servisler"** launch profile (`RotaApp.slnLaunch`), or individually with `dotnet run --project src/Services/Orders/Rota.Orders.Api`.

| | URL |
|---|---|
| Gateway | http://localhost:5000 |
| Orders · Dispatch · Tracking · Notifications · Ops API | http://localhost:5101 … 5105 |
| Health | `/health/live`, `/health/ready` on every service |
| Trace/log dashboard | http://localhost:18888 |
| RabbitMQ management | http://localhost:15672 |
