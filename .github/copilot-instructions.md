# Copilot Instructions — local-infrastructure

## Project Overview

**local-infrastructure** is a Docker-based local development infrastructure stack for .NET development and testing. It runs MS SQL Server 2025, PostgreSQL 16, and the Azure Service Bus Emulator in a fully license-free containerised environment so developers can exercise complete application stacks without cloud costs or external service dependencies.

## Tech Stack

| Layer | Technology |
|---|---|
| Container orchestration | Docker Compose v2+ |
| Container runtime | Docker Engine, Rancher Desktop, or Podman (all license-free) |
| Relational databases | MS SQL Server 2025 Developer Edition, PostgreSQL 16 (Alpine) |
| Message broker | Azure Service Bus Emulator (AMQP on port 5672, management on 5300) |
| Stack management script | PowerShell 5.1+ (`stack.ps1`) |
| Service Bus monitor tool | C# / .NET 8.0 (`servicebus/monitor/Program.cs`) |
| Configuration | Environment variables via `.env` file, JSON for Service Bus entities (`Config.example.json` → `Config.json`) |

## File Structure

```
.
├── .env.example             # Template — copy to .env and set real passwords
├── .gitignore               # Excludes .env, Config.json, build artifacts, OS files
├── docker-compose.yml       # All service definitions with health checks (servicebus monitored externally)
├── stack.ps1                # PowerShell stack manager (start/stop/restart/nuke/status/logs/pull/monitor)
├── README.md                # Comprehensive user documentation
└── servicebus/
    ├── Config.example.json   # Template — copy to Config.json and customise queues/topics
    └── monitor/
        ├── Program.cs        # Interactive TUI Service Bus message monitor (.NET 8)
        └── ServiceBusMonitor.csproj  # .NET project file (targets net8.0)
```

## Docker Services

| Service | Container Name | Image | Default Ports | Health Check |
|---|---|---|---|---|
| MS SQL Server 2025 | `local-mssql` | `mcr.microsoft.com/mssql/server:2025-latest` | 1433 | `sqlcmd SELECT 1` (15 s interval) |
| PostgreSQL 16 | `local-postgres` | `postgres:16-alpine` | 5432 | `pg_isready` (10 s interval) |
| Service Bus SQL (internal) | `local-servicebus-sql` | `mcr.microsoft.com/mssql/server:2025-latest` | none (internal) | `sqlcmd SELECT 1` (15 s interval) |
| Service Bus Emulator | `local-servicebus` | `mcr.microsoft.com/azure-messaging/servicebus-emulator:latest` | 5672, 5300 | Host-side HTTP GET `/health` via stack.ps1 (container has no shell) |

All services use `restart: unless-stopped`, named volumes for persistence, and proper `depends_on` with health conditions.

## Coding Conventions

### PowerShell (`stack.ps1`)

- Target **PowerShell 5.1+** (`#Requires -Version 5.1`). Avoid syntax only available in PowerShell 7+ (e.g. ternary operators, null-coalescing, pipeline chain operators, multi-argument `Join-Path`).
- Enable strict mode: `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'`.
- Use ANSI colour output with automatic fallback for non-ANSI terminals.
- Use Unicode box-drawing characters (`╭╮╰╯│─`) and status symbols (`✓ ✗ ⚠ ▶`) for rich TUI output.
- Validate prerequisites before every operation (Docker daemon running, `.env` file present).
- Organise functions following the pattern: `Invoke-Compose`, `Get-CStatus`, `Get-CHealth`, `Wait-AllHealthy`, `Show-Status`, `Assert-*`, then `Invoke-<Action>`.
- Use comment section headers with unicode line-drawing: `# ─── Section ───`.

### C# / .NET (`servicebus/monitor/`)

- Target **.NET 8.0** with `<ImplicitUsings>enable</ImplicitUsings>` and `<Nullable>enable</Nullable>`.
- Use modern C# features: records, nullable reference types, async/await, `ConcurrentDictionary`, `ConcurrentQueue`.
- Mirror the PowerShell ANSI colour scheme and TUI styling (box drawing, status symbols).
- Handle errors gracefully with retry logic and exponential backoff.
- Use comment section dividers matching the PowerShell style: `// ─── Section ───`.
- Cap in-memory collections to prevent memory exhaustion (e.g. 1000 messages max).

### Docker Compose (`docker-compose.yml`)

- All services are health-checked; `servicebus` is probed from the host via stack.ps1 because its image lacks a shell (no Docker `healthcheck` block).
- Use environment variable substitution with defaults: `${VAR:-default}`.
- All secrets must come from the `.env` file — never hard-code passwords.
- Define named volumes for any persistent data.
- Add descriptive comment headers for each service section.

### JSON Configuration (`servicebus/Config.example.json`)

- Clean, well-indented formatting (2-space indent).
- Follow the Azure Service Bus Emulator schema for namespaces, queues, topics, and subscriptions.
- `Config.example.json` is the safe-to-commit template; `Config.json` is the user's local copy (git-ignored).

### General

- Use inline comments to explain *why*, not *what*.
- Provide clear, actionable error messages for troubleshooting.
- Degrade gracefully when optional features are unavailable (e.g. ANSI colours).
- Never commit secrets — the `.env` file is in `.gitignore`.

## Environment Variables

All sensitive values live in a `.env` file (never committed). Copy `.env.example` to `.env` and fill in real values.

| Variable | Purpose | Constraints |
|---|---|---|
| `MSSQL_SA_PASSWORD` | SQL Server SA password | Min 8 chars, upper + lower + digit + special |
| `MSSQL_PORT` | Host port for SQL Server | Default 1433 |
| `POSTGRES_USER` | PostgreSQL superuser name | — |
| `POSTGRES_PASSWORD` | PostgreSQL password | — |
| `POSTGRES_DB` | Default database name | — |
| `POSTGRES_PORT` | Host port for PostgreSQL | Default 5432 |
| `SERVICEBUS_SQL_PASSWORD` | SA password for Service Bus internal SQL | Same rules as `MSSQL_SA_PASSWORD` |
| `SERVICEBUS_PORT` | Host port for AMQP | Default 5672 |
| `SERVICEBUS_MGMT_PORT` | Host port for management/health API | Default 5300 |

## Connection Strings

**MS SQL Server:**
```
Server=localhost,1433;User Id=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=True;
```

**PostgreSQL:**
```
Host=localhost;Port=5432;Database=<POSTGRES_DB>;Username=<POSTGRES_USER>;Password=<POSTGRES_PASSWORD>;
```

**Azure Service Bus Emulator:**
```
Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;
```
`SAS_KEY_VALUE` can be any non-empty string. Requires `Azure.Messaging.ServiceBus` ≥ 7.18.0.

## Running the Stack

```powershell
cp .env.example .env          # create secrets file and fill in real values
cp servicebus/Config.example.json servicebus/Config.json  # create Service Bus config
.\stack.ps1 start             # start all services and wait for healthy
.\stack.ps1 status            # verify health of every container
.\stack.ps1 logs              # stream live logs
.\stack.ps1 logs mssql        # stream logs from a single service
.\stack.ps1 monitor           # interactive Service Bus message monitor
.\stack.ps1 stop              # stop containers, keep data volumes
.\stack.ps1 nuke              # destroy containers AND volumes (data loss)
```

Or use Docker Compose directly:

```bash
docker compose up -d          # start all services
docker compose down           # stop containers, keep volumes
docker compose down -v        # stop and delete volumes
```

## Building the Service Bus Monitor

```bash
cd servicebus/monitor
dotnet build
dotnet run
```

The monitor auto-discovers topics and subscriptions from `servicebus/Config.json` (copied from `Config.example.json`) and peeks messages in real time with an interactive TUI.

## Key Guidelines for Changes

1. **Health checks are critical** — all services are health-checked; `servicebus` is probed from the host via stack.ps1 (no in-container healthcheck) so `stack.ps1` and `depends_on` conditions work correctly.
2. **Never commit `.env` or `servicebus/Config.json`** — both contain local configuration and are git-ignored.
3. **Test on PowerShell 5.1+** — the management script targets PowerShell 5.1 for broad compatibility; do not introduce PowerShell 7+-only syntax.
4. **Maintain the TUI styling** — both `stack.ps1` and `Program.cs` share a consistent ANSI/Unicode visual style; keep it consistent when modifying either.
5. **Service Bus emulator depends on its internal SQL Server** — changes to `servicebus-sql` can break `servicebus`.
6. **Volumes preserve data across restarts** — only `nuke` destroys volumes.
7. **No CI/CD pipelines exist** — this is an infrastructure-only repository.
8. **Password complexity** — SQL Server passwords must meet complexity rules (min 8 chars, mixed case, digit, special char).
9. **Keep `Config.example.json` updated** — changes to Service Bus entity definitions should be reflected in the example file.
