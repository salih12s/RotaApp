-- One database per service (data ownership, see docs/03-architecture.md).
-- Runs once, on first start of an empty volume.
CREATE DATABASE rota_orders;
CREATE DATABASE rota_dispatch;
CREATE DATABASE rota_tracking;
CREATE DATABASE rota_notifications;
CREATE DATABASE rota_ops;
CREATE DATABASE keycloak;
