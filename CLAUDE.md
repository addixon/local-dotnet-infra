# CLAUDE.md — local-infrastructure

## Project Overview

**local-infrastructure** is a Docker-based local development infrastructure stack for .NET development and testing. It provides MS SQL Server 2025 (Developer Edition), PostgreSQL 16, and the Azure Service Bus Emulator in a fully license-free containerised environment, eliminating the need for cloud costs or external service dependencies during development.

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
    ├── Config.json           # Azure Service Bus emulator entity config (queues, topics, subscriptions)
    └── monitor/
        ├── Program.cs        # Interactive TUI for monitoring Service Bus messages (.NET 8 / C#)
        └── ServiceBusMonitor.csproj  # .NET 8 project file
```

## Tech Stack

- **Docker Compose v2+** for container orchestration
- **MS SQL Server 2025 Developer Edition** (free for dev/test)
- **PostgreSQL 16** (Alpine variant)
- **Azure Service Bus Emulator** with AMQP protocol
- **PowerShell 7.0+** for the stack management script (`stack.ps1`)
- **C# / .NET 8.0** for the Service Bus monitor tool
- **License-free runtimes**: Docker Engine, Rancher Desktop, or Podman (no Docker Desktop required)

## Docker Services

Four containers run in the stack:

1. **`local-mssql`** — MS SQL Server 2025 Developer on port 1433. Health checked with `sqlcmd`.
2. **`local-postgres`** — PostgreSQL 16 Alpine on port 5432. Health checked with `pg_isready`.
3. **`local-servicebus-sql`** — Internal SQL Server for the Service Bus emulator (no exposed port). Health checked with `sqlcmd`.
4. **`local-servicebus`** — Azure Service Bus Emulator on ports 5672 (AMQP) and 5300 (management/health). Health checked with HTTP GET to `/health`. Depends on `servicebus-sql` being healthy.

All services use `restart: unless-stopped` and named Docker volumes for data persistence.

## Commands

### Stack Management (PowerShell)

```powershell
.\stack.ps1 start             # Start all services, wait for healthy
.\stack.ps1 stop              # Stop containers, preserve volumes
.\stack.ps1 restart           # Full stop/start cycle
.\stack.ps1 nuke              # Destroy containers AND volumes (data loss!)
.\stack.ps1 status            # Show container health/status
.\stack.ps1 logs [service]    # Stream logs (optionally for one service)
.\stack.ps1 pull              # Pull latest images
.\stack.ps1 monitor           # Interactive Service Bus message monitor TUI
```

### Docker Compose (Alternative)

```bash
docker compose up -d          # Start all services
docker compose down           # Stop, preserve volumes
docker compose down -v        # Stop and delete volumes
```

### Build the Service Bus Monitor

```bash
cd servicebus/monitor
dotnet build
dotnet run
```

## Environment Configuration

Copy `.env.example` → `.env` and fill in all placeholder values. **Never commit `.env`** (it is in `.gitignore`).

| Variable | Purpose | Notes |
|---|---|---|
| `MSSQL_SA_PASSWORD` | SQL Server SA password | Min 8 chars, upper + lower + digit + special character |
| `MSSQL_PORT` | Host port for SQL Server | Default: 1433 |
| `POSTGRES_USER` | PostgreSQL superuser name | — |
| `POSTGRES_PASSWORD` | PostgreSQL password | — |
| `POSTGRES_DB` | Default database name | — |
| `POSTGRES_PORT` | Host port for PostgreSQL | Default: 5432 |
| `SERVICEBUS_SQL_PASSWORD` | SA password for Service Bus internal SQL | Same complexity rules as `MSSQL_SA_PASSWORD` |
| `SERVICEBUS_PORT` | Host port for Service Bus AMQP | Default: 5672 |
| `SERVICEBUS_MGMT_PORT` | Host port for Service Bus management | Default: 5300 |

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

## Coding Conventions

### PowerShell (`stack.ps1`)

- Requires PowerShell 7.0+ (`#Requires -Version 7.0`).
- Strict mode enabled: `Set-StrictMode -Version Latest`, `$ErrorActionPreference = 'Stop'`.
- Uses ANSI colour codes with automatic fallback for terminals that do not support ANSI.
- Rich TUI output with Unicode box-drawing characters (`╭╮╰╯│─`) and status symbols (`✓ ✗ ⚠ ▶`).
- Validates prerequisites (Docker daemon running, `.env` file exists) before any operation.
- Comment section headers use Unicode line-drawing: `# ─── Section Name ───`.
- Functions follow naming convention: `Invoke-<Action>`, `Get-<Property>`, `Assert-<Condition>`, `Wait-<State>`, `Show-<View>`.

### C# / .NET (`servicebus/monitor/`)

- Targets .NET 8.0 with implicit usings and nullable reference types enabled.
- Uses modern C# features: records, async/await, `ConcurrentDictionary`, `ConcurrentQueue`.
- Mirrors the PowerShell ANSI colour scheme and TUI styling for visual consistency.
- Handles errors gracefully with retry logic and exponential backoff (1 s → 30 s max).
- Caps in-memory collections at 1000 items to prevent memory exhaustion.
- Comment section dividers: `// ─── Section Name ───`.

### Docker Compose (`docker-compose.yml`)

- Every service must have a `healthcheck` block.
- Secrets come from `.env` via environment variable substitution with defaults: `${VAR:-default}`.
- Named volumes for any persistent data. Comment headers for each service section.

### JSON (`servicebus/Config.json`)

- 2-space indentation. Follows the Azure Service Bus Emulator configuration schema.

### General Patterns

- Inline comments explain *why*, not *what*.
- Error messages are clear and actionable.
- Graceful degradation when optional features are unavailable.
- No secrets in source — `.env` is git-ignored.

## Important Rules

1. **Health checks are essential** — every Docker service must have one. `stack.ps1` and `depends_on` conditions rely on them.
2. **Never commit `.env`** — it contains secrets and is git-ignored.
3. **Password complexity** — SQL Server passwords must meet: min 8 characters, mixed case, at least one digit, at least one special character.
4. **Service Bus depends on its internal SQL Server** — `servicebus` starts only after `servicebus-sql` is healthy.
5. **Maintain TUI consistency** — `stack.ps1` and `Program.cs` share a visual style; preserve it in any changes.
6. **Volumes persist across restarts** — only `.\stack.ps1 nuke` or `docker compose down -v` removes them.
7. **No CI/CD pipelines** — this is an infrastructure-only repository with no automated test suite.
8. **No test infrastructure** — there are no unit or integration tests to run.
9. **PowerShell 7.0+ only** — do not use features removed in PowerShell 7 or syntax incompatible with it.
10. **Keep `.env.example` in sync** — if you add new environment variables to `docker-compose.yml`, add them to `.env.example` with placeholder values and document them in `README.md`.

## Troubleshooting

- **Unhealthy containers**: Check password complexity, review logs with `.\stack.ps1 logs <service>`.
- **Port conflicts**: Change port values in `.env` and restart.
- **Startup timeout (>120 s)**: Pull images first with `.\stack.ps1 pull`; check Docker resource limits.
- **Can't connect to SQL Server**: Verify `TrustServerCertificate=True` is in the connection string.
- **Service Bus not starting**: Ensure `servicebus-sql` is healthy first; check its logs.
