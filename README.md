# local-infrastructure

A local Docker-based infrastructure stack for .NET development and testing. Runs **MS SQL Server**, **PostgreSQL**, and the **Azure Service Bus Emulator** in a fully license-free environment so you can exercise complete stacks without any cloud costs or service dependencies.

---

## Services

| Service | Image | Default Port |
|---|---|---|
| MS SQL Server 2022 (Developer) | `mcr.microsoft.com/mssql/server:2022-latest` | `1433` |
| PostgreSQL 16 | `postgres:16-alpine` | `5432` |
| Azure Service Bus Emulator | `mcr.microsoft.com/azure-messaging/servicebus-emulator:latest` | `5672` (AMQP) |

> **License note** – MS SQL Server Developer Edition and the Azure Service Bus Emulator are both free for local development and testing. No paid Docker Desktop subscription is required; use [Docker Engine](https://docs.docker.com/engine/install/) on Linux, [Rancher Desktop](https://rancherdesktop.io/), or [Podman Desktop](https://podman-desktop.io/) on any OS.

---

## Prerequisites

- A license-free Docker-compatible runtime (Docker Engine, Rancher Desktop, or Podman Desktop)
- `docker compose` CLI (v2+)

---

## Quick start

### 1. Clone and configure secrets

```bash
git clone https://github.com/addixon/local-infrastructure.git
cd local-infrastructure

# Copy the example file and fill in your own passwords
cp .env.example .env
```

Open `.env` in your editor and replace every placeholder value with a real password. See [`.env` reference](#env-reference) below.

### 2. Start the stack

```bash
docker compose up -d
```

All services start in the background. The SQL-backed Service Bus emulator waits for its internal SQL Server to pass health checks before starting.

### 3. Stop the stack

```bash
docker compose down          # stop containers, keep volumes
docker compose down -v       # stop containers and delete all volumes (data loss!)
```

---

## `.env` reference

Copy `.env.example` to `.env` and replace the placeholder values. **Never commit the real `.env` file** — it is already listed in `.gitignore`.

```dotenv
# ─── MS SQL Server ───────────────────────────────────────────────────────────
# SA (system-administrator) password.
# Must meet SQL Server complexity requirements:
#   min 8 chars, upper + lower + digit + special character.
MSSQL_SA_PASSWORD=YourStrong(!)Passw0rd

# Host port to expose SQL Server on (default: 1433)
MSSQL_PORT=1433

# ─── PostgreSQL ──────────────────────────────────────────────────────────────
POSTGRES_USER=localuser
POSTGRES_PASSWORD=YourPostgresPassword1!
POSTGRES_DB=localdb

# Host port to expose PostgreSQL on (default: 5432)
POSTGRES_PORT=5432

# ─── Azure Service Bus Emulator ──────────────────────────────────────────────
# SA password for the internal SQL Server used by the Service Bus emulator.
# Must meet the same SQL Server complexity requirements as MSSQL_SA_PASSWORD.
SERVICEBUS_SQL_PASSWORD=YourStrong(!)Passw0rd

# Host port to expose the Service Bus AMQP endpoint on (default: 5672)
SERVICEBUS_PORT=5672
```

---

## Connection strings

### MS SQL Server

```
Server=localhost,1433;User Id=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=True;
```

### PostgreSQL

```
Host=localhost;Port=5432;Database=<POSTGRES_DB>;Username=<POSTGRES_USER>;Password=<POSTGRES_PASSWORD>;
```

### Azure Service Bus Emulator

Use the well-known development connection string (the emulator accepts it regardless of your namespace name):

```
Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;
```

> The `UseDevelopmentEmulator=true` flag is supported by **Azure.Messaging.ServiceBus** ≥ 7.18.0.

---

## Customising Service Bus queues and topics

Edit [`servicebus/Config.json`](servicebus/Config.json) before starting the stack (or restart the `servicebus` container after editing). The file defines the namespace, queues, and topics the emulator exposes. The default configuration ships with one example queue (`queue.1`) and one topic (`topic.1`) with one subscription (`subscription.1`).

---

## File structure

```
.
├── .env.example          # Safe-to-commit template — copy to .env and fill in secrets
├── .gitignore            # Ensures .env is never committed
├── docker-compose.yml    # All service definitions
├── servicebus/
│   └── Config.json       # Azure Service Bus emulator namespace / queue / topic config
└── README.md
```
