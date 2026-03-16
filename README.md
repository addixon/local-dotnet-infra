# Local Infrastructure

A Docker-based local development stack providing **MS SQL Server 2025**, **PostgreSQL 16**, and the **Azure Service Bus Emulator** — everything you need to develop and test .NET applications locally without cloud costs, paid licenses, or external service dependencies.

---

## Table of Contents

- [Services](#services)
- [Prerequisites](#prerequisites)
- [Installing Docker on Windows (license-free)](#installing-docker-on-windows-license-free)
- [Managing the stack](#managing-the-stack)
- [Service Bus only](#service-bus-only)
- [Quick start](#quick-start)
- [`.env` reference](#env-reference)
- [Connection strings](#connection-strings)
- [Customising Service Bus queues and topics](#customising-service-bus-queues-and-topics)
- [Service Bus Monitor](#service-bus-monitor)
- [Troubleshooting](#troubleshooting)
- [File structure](#file-structure)

---

## Services

| Service | Image | Default Port(s) |
|---|---|---|
| MS SQL Server 2025 (Developer) | `mcr.microsoft.com/mssql/server:2025-latest` | `1433` |
| PostgreSQL 16 | `postgres:16-alpine` | `5432` |
| Service Bus Emulator Proxy | `.NET 10 (custom)` | `5672` (AMQP), `5300` (management/health), `443` (HTTPS admin) |

The **Service Bus Emulator Proxy** transparently shards topics and queues across multiple Azure Service Bus Emulator instances (50 entities per emulator). Applications connect to a single AMQP + HTTP endpoint and are unaware of the underlying sharding. The proxy dynamically spins up additional emulators as needed.

> **License note** – MS SQL Server Developer Edition and the Azure Service Bus Emulator are both free for local development and testing. No paid Docker Desktop subscription is required; install **Rancher Desktop** via `winget install SUSE.RancherDesktop` on Windows, use [Docker Engine](https://docs.docker.com/engine/install/) on Linux, or [Podman Desktop](https://podman-desktop.io/) on any OS.

---

## Prerequisites

- A license-free Docker-compatible runtime (see [Installing Docker on Windows](#installing-docker-on-windows-license-free) below, or use Docker Engine on Linux)
- `docker compose` CLI (v2+)
- [PowerShell 5.1+](https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell) (required by `stack.ps1` / `sb.ps1`; optional if using `docker compose` directly)

---

## Installing Docker on Windows (license-free)

The recommended license-free Docker runtime on Windows is **Rancher Desktop**, which ships a full Docker-compatible CLI and container engine with no subscription required.

Install it with a single WinGet command (run in an **elevated** PowerShell or Command Prompt):

```powershell
winget install SUSE.RancherDesktop
```

Rancher Desktop will install `docker` and `docker compose` and make them available on your `PATH`. Reopen your terminal after installation to pick up the new path entries.

> **Tip** – During first launch you will be prompted to choose a container runtime. Select **dockerd (moby)** to get a fully Docker-compatible socket that works with `docker compose`.

### Alternative: Podman Desktop

If you prefer Podman, it is also available via WinGet:

```powershell
winget install RedHat.Podman-Desktop
```

> Podman Desktop includes a Docker compatibility layer. After enabling it you can use `docker compose` commands unchanged.

---

## Managing the stack

Use `stack.ps1` for all day-to-day operations. It validates prerequisites, runs
the appropriate `docker compose` commands, and verifies the outcome after each action.

```powershell
.\stack.ps1 <action> [service]
```

| Action | Description |
|---|---|
| `start` | Bring up all services; polls until every container is healthy |
| `stop` | Stop and remove containers (data volumes are preserved) |
| `restart` | Full stop/start cycle; waits until healthy |
| `nuke` | Destroy all containers **and** volumes ⚠ — all data is lost |
| `status` | Print the current health/status of every container |
| `logs` | Stream live logs from all services (Ctrl+C to stop) |
| `logs <service>` | Stream logs from one service (`mssql`, `postgres`, `servicebus-proxy`) |
| `pull` | Pull the latest images without starting the stack |
| `monitor` | Launch the interactive Service Bus message monitor TUI (see [Service Bus Monitor](#service-bus-monitor)) |

Running `.\stack.ps1` with no arguments prints the usage summary.

---

## Service Bus only

If you only need the Azure Service Bus Emulator Proxy — without MS SQL Server or
PostgreSQL — use `sb.ps1` instead. It supports the same actions as `stack.ps1`
but only starts the Service Bus portion of the stack.

```powershell
.\sb.ps1 <action> [service]
```

| Action | Description |
|---|---|
| `start` | Bring up the Service Bus proxy; polls until healthy |
| `stop` | Stop and remove Service Bus containers (data volumes preserved) |
| `restart` | Full stop/start cycle; waits until healthy |
| `nuke` | Destroy Service Bus containers **and** volumes ⚠ — database services are NOT affected |
| `status` | Print the current health/status of Service Bus containers |
| `logs` | Stream live logs from the Service Bus proxy (Ctrl+C to stop) |
| `logs servicebus-proxy` | Stream logs from the proxy service |
| `pull` | Pull the latest proxy image without starting |
| `monitor` | Launch the interactive Service Bus message monitor TUI |

The `.env` file is still required but only `SERVICEBUS_SQL_PASSWORD` (and optionally the
`SERVICEBUS_PORT`, `SERVICEBUS_MGMT_PORT`, `SERVICEBUS_ADMIN_PORT` overrides) need to be set.

---

## Quick start

### 1. Clone and configure

```bash
git clone https://github.com/addixon/local-infrastructure.git
cd local-infrastructure

# Copy example files and fill in your own values
cp .env.example .env
cp servicebus/Config.example.json servicebus/Config.json
```

Open `.env` in your editor and replace every placeholder value with a real password. See [`.env` reference](#env-reference) below.

Optionally edit `servicebus/Config.json` to customise queues, topics, and subscriptions (see [Customising Service Bus](#customising-service-bus-queues-and-topics)).

### 2. Start the stack

**Recommended** – Use the PowerShell stack manager for automatic health verification:

```powershell
.\stack.ps1 start
```

This starts all services, polls health checks, and reports when the stack is fully ready.

**Alternative** – Use Docker Compose directly (no automatic health verification):

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
MSSQL_SA_PASSWORD=<REPLACE_WITH_YOUR_PASSWORD>

# Host port to expose SQL Server on (default: 1433)
MSSQL_PORT=1433

# ─── PostgreSQL ──────────────────────────────────────────────────────────────
POSTGRES_USER=<REPLACE_WITH_USERNAME>
POSTGRES_PASSWORD=<REPLACE_WITH_YOUR_PASSWORD>
POSTGRES_DB=<REPLACE_WITH_DATABASE_NAME>

# Host port to expose PostgreSQL on (default: 5432)
POSTGRES_PORT=5432

# ─── Azure Service Bus Emulator Proxy ────────────────────────────────────────
# SA password for the SQL Server instances used by each Service Bus emulator.
# Must meet the same SQL Server complexity requirements as MSSQL_SA_PASSWORD.
SERVICEBUS_SQL_PASSWORD=<REPLACE_WITH_YOUR_PASSWORD>

# Host port to expose the Service Bus AMQP endpoint on (default: 5672)
# Applications connect to this port — the proxy routes to backend emulators.
SERVICEBUS_PORT=5672

# Host port to expose the Service Bus management/health endpoint on (default: 5300)
# The proxy aggregates health and management API from all backend emulators.
SERVICEBUS_MGMT_PORT=80

# Host port to expose the Service Bus HTTPS admin endpoint on (default: 443)
# The ServiceBusAdministrationClient (used by MassTransit etc.) connects here.
# A self-signed TLS certificate is generated at proxy startup.
SERVICEBUS_ADMIN_PORT=443
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

The emulator accepts a well-known development connection string (works regardless of your configured namespace name):

```
Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;
```

Replace `SAS_KEY_VALUE` with any non-empty string (the emulator doesn't validate it). Example:

```
Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=DEVKEY123;UseDevelopmentEmulator=true;
```

> The `UseDevelopmentEmulator=true` flag is supported by **Azure.Messaging.ServiceBus** ≥ 7.18.0.

---

## Customising Service Bus queues and topics

Copy `servicebus/Config.example.json` to `servicebus/Config.json` (if you haven't already) and edit it to define the namespace, queues, and topics the emulator exposes. The config file uses the same format as the Azure Service Bus Emulator's `Config.json`, but **you can define more than 50 entities** — the proxy will automatically shard them across multiple emulator instances.

The example configuration ships with one topic (`topic.1`) and one subscription.

**Never commit `servicebus/Config.json`**

Restart the stack after editing the config file: `.\stack.ps1 restart` (or `.\sb.ps1 restart` for Service Bus only)

---

## Service Bus Emulator Proxy

The proxy (`servicebus/proxy/`) is a .NET 10 application that sits between your application and one or more Azure Service Bus Emulator instances. It solves the emulator's **50-entity limit** by transparently sharding entities across multiple emulators.

### Architecture

```
┌─────────────────────────────────────┐
│  Your Application                   │
│  (Azure.Messaging.ServiceBus SDK)   │
└──────────┬──────────────────────────┘
           │  AMQP (5672) + HTTP (5300) + HTTPS (443)
           ▼
┌─────────────────────────────────────┐
│  ServiceBus Emulator Proxy          │
│  - Reads master Config.json         │
│  - Shards entities (50 per shard)   │
│  - Routes AMQP by entity name       │
│  - Aggregates management API        │
│  - Dynamic scaling via Docker API   │
└──┬──────────┬──────────┬────────────┘
   │          │          │
   ▼          ▼          ▼
┌───────┐ ┌───────┐ ┌───────┐
│ SB #0 │ │ SB #1 │ │ SB #2 │  ...
│ +SQL  │ │ +SQL  │ │ +SQL  │
└───────┘ └───────┘ └───────┘
```

### How it works

1. **On startup**, the proxy reads `servicebus/Config.json` and splits entities into groups of 50
2. For each group, it creates a SQL Server container and a Service Bus Emulator container (via the Docker API)
3. It starts an **AMQP server** on port 5672 that routes messages to the correct backend emulator
4. It starts an **HTTP server** on port 5300 that aggregates the management API and health endpoint
5. It starts an **HTTPS server** on port 443 so that `ServiceBusAdministrationClient` (used by MassTransit and similar libraries) can connect transparently — a self-signed TLS certificate is generated at startup (or a trusted cert mounted from the host by `stack.ps1` / `sb.ps1`)
6. **Dynamic scaling**: if an application creates a topic via the management API that would exceed an emulator's capacity, the proxy spins up a new emulator

### Proxy endpoints

| Endpoint | Purpose |
|---|---|
| `http://localhost:5300/health` | Aggregate health of all emulators |
| `http://localhost:5300/_proxy/status` | Detailed proxy status (shard count, entity count, per-emulator health) |
| `http://localhost:5300/_proxy/entities` | Entity registry — lists all topics with their subscriptions (used by the monitor) |
| `https://localhost:443/...` | HTTPS admin endpoint for `ServiceBusAdministrationClient` (e.g. MassTransit topology creation) |

### Entity limit

The Azure Service Bus Emulator supports a maximum of **50 entities (queues + topics combined)** per namespace. The proxy removes this limitation by distributing entities across multiple emulator instances.

---

## Service Bus Monitor

The repository includes an interactive terminal-based (TUI) message monitor for the Azure Service Bus Emulator, built with .NET 8 and C#. It lets you peek at messages flowing through your topics and subscriptions in real time — useful for debugging and verifying message-driven workflows during local development.

### Launching the monitor

The easiest way to start the monitor is through the stack manager:

```powershell
.\stack.ps1 monitor
# or, for Service Bus only (no databases):
.\sb.ps1 monitor
```

This automatically builds the .NET project (if needed) and launches the interactive TUI.

Alternatively, build and run it directly:

```bash
cd servicebus/monitor
dotnet build
dotnet run
```

### How it works

- **Auto-discovers** topics and subscriptions dynamically via the proxy's management API (`/_proxy/entities`), with optional seeding from `servicebus/Config.json` for backwards compatibility.
- **Peeks** messages without consuming them, so your application's subscribers are unaffected.
- Monitors all subscriptions discovered from the proxy — no dedicated subscription required.
- Provides keyboard navigation to browse messages across topics.
- Retries with exponential backoff if the Service Bus proxy is not yet available.
- Caps in-memory message storage at 1,000 messages to prevent memory exhaustion.

---

## Troubleshooting

### Containers show as unhealthy

If `.\stack.ps1 status` or `docker ps` shows containers as unhealthy:

1. **Check logs** for the failing service:
   ```powershell
   .\stack.ps1 logs <service-name>
   # Example: .\stack.ps1 logs mssql
   ```

2. **Common causes**:
   - **SQL Server container** (mssql): Password doesn't meet complexity requirements (min 8 chars, upper + lower + digit + special character)
   - **PostgreSQL**: Wrong credentials in `.env` file
   - **Service Bus Proxy**: Docker socket not accessible, or SQL password not set in `.env`
   - **Service Bus Emulators**: Proxy logs will show which emulator failed to start

3. **Reset and try again**:
   ```powershell
   .\stack.ps1 stop
   .\stack.ps1 start
   ```

### Port already in use

If you see "port is already allocated" errors:

1. **Check what's using the port**:
   ```powershell
   # Windows
   netstat -ano | findstr :<port>
   # Example: netstat -ano | findstr :1433

   # Linux/Mac
   lsof -i :<port>
   ```

2. **Change the port** in `.env`:
   ```env
   MSSQL_PORT=1434        # instead of 1433
   POSTGRES_PORT=5433     # instead of 5432
   SERVICEBUS_PORT=5673   # instead of 5672
   ```

3. **Restart the stack**:
   ```powershell
   .\stack.ps1 restart
   ```

### Stack takes too long to start

If `.\stack.ps1 start` times out after 300 seconds:

1. **Check Docker resources** – Ensure Docker has enough CPU and memory allocated (especially SQL Server)

2. **Pull images first** to avoid download time during startup:
   ```powershell
   .\stack.ps1 pull
   ```

3. **Check logs** to see which service is slow:
   ```powershell
   .\stack.ps1 logs
   ```

### Can't connect to SQL Server

If your application can't connect:

1. **Verify container is healthy**:
   ```powershell
   .\stack.ps1 status
   ```

2. **Check connection string** uses `TrustServerCertificate=True`:
   ```
   Server=localhost,1433;User Id=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=True;
   ```

3. **Test connectivity** with sqlcmd:
   ```bash
   docker exec -it local-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "<MSSQL_SA_PASSWORD>" -C -Q "SELECT @@VERSION"
   ```

---

## File structure

```
.
├── .env.example              # Safe-to-commit template — copy to .env and fill in secrets
├── .gitignore                # Ensures .env and Config.json are never committed
├── docker-compose.yml        # All service definitions with health checks
├── stack.ps1                 # PowerShell stack manager — full stack (start/stop/restart/nuke/status/logs/pull/monitor)
├── sb.ps1                    # PowerShell stack manager — Service Bus only (same actions, no databases)
├── README.md                 # This documentation
├── docs/
│   ├── FUTURE-MULTIPLEXING.md    # Spec for full AMQP connection multiplexing
│   └── FUTURE-ASYNC-SPINUP.md   # Spec for async emulator spin-up
├── test-amqp/                # AMQP test client
│   └── Program.cs
└── servicebus/
    ├── Config.example.json   # Safe-to-commit template — copy to Config.json and customise
    ├── emulator-configs/     # Auto-generated per-emulator config shards (gitignored)
    ├── proxy-cert/           # TLS certificate for HTTPS admin endpoint (generated by stack.ps1 or sb.ps1, gitignored)
    ├── monitor/
    │   ├── Program.cs                # Interactive Service Bus message monitor (.NET 8 TUI)
    │   └── ServiceBusMonitor.csproj  # .NET 8 project file
    └── proxy/
        ├── Dockerfile                # Container image for the proxy
        ├── Program.cs                # Entry point — wires AMQP + HTTP + HTTPS proxy
        ├── ServiceBusProxy.csproj    # .NET 10 project file
        ├── Models/
        │   └── ProxyConfig.cs        # Config, state, and runtime models
        └── Services/
            ├── AmqpProxy.cs              # AMQP proxy server + message relay
            ├── ConfigShardManager.cs     # Config sharding (50 entities per shard)
            ├── EmulatorOrchestrator.cs   # Docker container lifecycle management
            ├── EntityRouter.cs           # Entity → emulator routing
            ├── ManagementProxy.cs        # HTTP management API proxy
            └── StateManager.cs           # State persistence
```
