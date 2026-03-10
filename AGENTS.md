# AGENTS.md — local-infrastructure

## Project Overview

**local-infrastructure** is a Docker-based local development infrastructure stack for .NET development and testing. It runs MS SQL Server 2025 (Developer Edition), PostgreSQL 16, and the Azure Service Bus Emulator in a fully license-free containerised environment so developers can exercise complete application stacks without cloud costs or external service dependencies.

Repository: `https://github.com/addixon/local-infrastructure`

## File Structure

```
.
├── .env.example             # Template — copy to .env and set passwords
├── .gitignore               # Excludes .env, build artifacts, OS files
├── docker-compose.yml       # Docker Compose service definitions with health checks
├── stack.ps1                # PowerShell 7.0+ stack manager (start/stop/restart/nuke/status/logs/pull/monitor)
├── README.md                # Full user documentation
└── servicebus/
    ├── Config.json           # Azure Service Bus emulator entity configuration (queues, topics, subscriptions)
    └── monitor/
        ├── Program.cs        # Interactive TUI for monitoring Service Bus messages (.NET 8 / C#)
        └── ServiceBusMonitor.csproj  # .NET 8 project file
```

## Tech Stack

| Layer | Technology |
|---|---|
| Container orchestration | Docker Compose v2+ |
| Container runtime | Docker Engine, Rancher Desktop, or Podman (all license-free) |
| Relational databases | MS SQL Server 2025 Developer Edition, PostgreSQL 16 (Alpine) |
| Message broker | Azure Service Bus Emulator (AMQP on port 5672, management on 5300) |
| Stack management script | PowerShell 7.0+ (`stack.ps1`) |
| Service Bus monitor tool | C# / .NET 8.0 (`servicebus/monitor/Program.cs`) |
| Configuration | Environment variables via `.env`, JSON for Service Bus entities |

## Docker Services

Four containers make up the stack:

| Service | Container Name | Image | Ports | Health Check |
|---|---|---|---|---|
| MS SQL Server 2025 | `local-mssql` | `mcr.microsoft.com/mssql/server:2025-latest` | 1433 | `sqlcmd SELECT 1` (15 s interval) |
| PostgreSQL 16 | `local-postgres` | `postgres:16-alpine` | 5432 | `pg_isready` (10 s interval) |
| Service Bus SQL (internal) | `local-servicebus-sql` | `mcr.microsoft.com/mssql/server:2025-latest` | none | `sqlcmd SELECT 1` (15 s interval) |
| Service Bus Emulator | `local-servicebus` | `mcr.microsoft.com/azure-messaging/servicebus-emulator:latest` | 5672, 5300 | HTTP GET `/health` (10 s interval) |

All services use `restart: unless-stopped`, named Docker volumes for persistence, and proper `depends_on` with health conditions.

## Environment Configuration

Copy `.env.example` to `.env` and replace all placeholder values. **Never commit `.env`** — it is in `.gitignore`.

| Variable | Purpose | Constraints |
|---|---|---|
| `MSSQL_SA_PASSWORD` | SQL Server SA password | Min 8 chars, upper + lower + digit + special character |
| `MSSQL_PORT` | Host port for SQL Server | Default: 1433 |
| `POSTGRES_USER` | PostgreSQL superuser name | — |
| `POSTGRES_PASSWORD` | PostgreSQL password | — |
| `POSTGRES_DB` | Default database name | — |
| `POSTGRES_PORT` | Host port for PostgreSQL | Default: 5432 |
| `SERVICEBUS_SQL_PASSWORD` | SA password for Service Bus internal SQL | Same complexity rules as `MSSQL_SA_PASSWORD` |
| `SERVICEBUS_PORT` | Host port for AMQP | Default: 5672 |
| `SERVICEBUS_MGMT_PORT` | Host port for management/health | Default: 5300 |

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

## Commands

### Stack Management (PowerShell)

```powershell
.\stack.ps1 start             # Start services, wait for healthy
.\stack.ps1 stop              # Stop containers, preserve volumes
.\stack.ps1 restart           # Full stop/start cycle
.\stack.ps1 nuke              # Destroy containers AND volumes (data loss!)
.\stack.ps1 status            # Show container health/status
.\stack.ps1 logs [service]    # Stream logs (optionally for one service: mssql, postgres, servicebus, servicebus-sql)
.\stack.ps1 pull              # Pull latest images
.\stack.ps1 monitor           # Interactive Service Bus message monitor TUI
```

### Docker Compose (Alternative)

```bash
docker compose up -d          # Start all services
docker compose down           # Stop, preserve volumes
docker compose down -v        # Stop and delete volumes
```

### Building the Service Bus Monitor

```bash
cd servicebus/monitor
dotnet build
dotnet run
```

The monitor auto-discovers topics and subscriptions from `servicebus/Config.json`, peeks messages in real time, and provides an interactive TUI with arrow-key navigation.

## Coding Conventions

### PowerShell (`stack.ps1`)

- **Version**: PowerShell 7.0+ (`#Requires -Version 7.0`).
- **Strict mode**: `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'`.
- **Terminal output**: ANSI colour codes with automatic fallback for terminals without ANSI support.
- **TUI elements**: Unicode box-drawing characters (`╭╮╰╯│─`) and status symbols (`✓ ✗ ⚠ ▶`).
- **Prerequisite checks**: Validate Docker daemon and `.env` presence before every operation.
- **Section headers**: `# ─── Section Name ───` using Unicode line-drawing characters.
- **Function naming**: `Invoke-<Action>`, `Get-<Property>`, `Assert-<Condition>`, `Wait-<State>`, `Show-<View>`.

### C# / .NET (`servicebus/monitor/`)

- **Framework**: .NET 8.0. Implicit usings and nullable reference types enabled.
- **Language features**: Records, async/await, `ConcurrentDictionary`, `ConcurrentQueue`.
- **Visual style**: Mirror the PowerShell ANSI colour scheme and TUI styling for consistency.
- **Error handling**: Retry logic with exponential backoff (1 s initial, 30 s cap).
- **Memory safety**: In-memory collections capped at 1000 items to prevent exhaustion.
- **Section headers**: `// ─── Section Name ───` to match PowerShell style.

### Docker Compose (`docker-compose.yml`)

- Every service must include a `healthcheck` block (with `interval`, `timeout`, `retries`, `start_period`).
- Use environment variable substitution with defaults: `${VAR:-default}`.
- Never hard-code secrets — all sensitive values come from `.env`.
- Define named volumes for persistent data.
- Descriptive comment headers for each service section.

### JSON (`servicebus/Config.json`)

- 2-space indentation. Follow the Azure Service Bus Emulator schema.

### General Patterns

- Comments explain *why*, not *what*.
- Error messages should be clear and actionable.
- Degrade gracefully when optional features are unavailable (e.g. ANSI terminal support).
- No secrets in source code — `.env` is always git-ignored.
- Keep `.env.example` and `README.md` in sync when adding environment variables.

## Important Rules

1. **Health checks are essential** — every Docker service needs one. `stack.ps1` and `depends_on` conditions depend on them.
2. **Never commit `.env`** — contains secrets; always git-ignored.
3. **SQL Server password complexity** — min 8 characters, mixed case, at least one digit, at least one special character.
4. **Service Bus dependency chain** — `servicebus` starts only after `servicebus-sql` passes health checks.
5. **Maintain TUI visual consistency** — `stack.ps1` and `Program.cs` share a design language; preserve it.
6. **Volumes persist data** — only `.\stack.ps1 nuke` or `docker compose down -v` removes them.
7. **No CI/CD pipelines** — this is an infrastructure-only repository.
8. **No test infrastructure** — there are no unit or integration tests.
9. **PowerShell 7.0+ only** — do not introduce syntax that breaks on PowerShell 7.
10. **Keep `.env.example` updated** — any new environment variables in `docker-compose.yml` must also appear in `.env.example` with placeholder values and be documented in `README.md`.

## Troubleshooting

- **Unhealthy containers**: Check password complexity; review logs with `.\stack.ps1 logs <service>`.
- **Port conflicts**: Change port values in `.env` and run `.\stack.ps1 restart`.
- **Startup timeout (>120 s)**: Pull images first with `.\stack.ps1 pull`; increase Docker memory allocation.
- **SQL Server connection issues**: Ensure `TrustServerCertificate=True` in the connection string.
- **Service Bus won't start**: Confirm `servicebus-sql` is healthy; review its logs.

## Service Bus Customisation

Edit `servicebus/Config.json` to add or remove queues, topics, and subscriptions. The default configuration includes:

- Namespace: `local-namespace`
- Queue: `queue.1`
- Topic: `topic.1` with subscription `subscription.1`

Restart the `servicebus` container after editing the config file.
