# Gemini Code Assist Style Guide — local-infrastructure

## Project Overview

**local-infrastructure** is a Docker-based local development infrastructure stack for .NET development and testing. It runs MS SQL Server 2025 (Developer Edition), PostgreSQL 16, and the Azure Service Bus Emulator in a fully license-free containerised environment so developers can exercise complete stacks without cloud costs or external service dependencies.

## File Structure

```
.
├── .env.example             # Template — copy to .env and set passwords
├── .gitignore               # Excludes .env, Config.json, build artifacts, OS files
├── docker-compose.yml       # Docker Compose service definitions with health checks
├── stack.ps1                # PowerShell 7.0+ stack manager
├── README.md                # Full user documentation
└── servicebus/
    ├── Config.example.json   # Template — copy to Config.json and customise queues/topics
    └── monitor/
        ├── Program.cs        # Interactive TUI for monitoring Service Bus messages (.NET 8 / C#)
        └── ServiceBusMonitor.csproj  # .NET 8 project file
```

## Tech Stack

- **Docker Compose v2+** — container orchestration
- **MS SQL Server 2025 Developer Edition** — relational database (port 1433)
- **PostgreSQL 16 Alpine** — relational database (port 5432)
- **Azure Service Bus Emulator** — message broker (AMQP port 5672, management port 5300)
- **PowerShell 7.0+** — stack management script (`stack.ps1`)
- **C# / .NET 8.0** — Service Bus monitor tool (`servicebus/monitor/`)
- **License-free runtimes only** — Docker Engine, Rancher Desktop, or Podman

## Docker Services

| Service | Container | Image | Ports | Health Check |
|---|---|---|---|---|
| MS SQL Server 2025 | `local-mssql` | `mcr.microsoft.com/mssql/server:2025-latest` | 1433 | `sqlcmd SELECT 1` |
| PostgreSQL 16 | `local-postgres` | `postgres:16-alpine` | 5432 | `pg_isready` |
| Service Bus SQL (internal) | `local-servicebus-sql` | `mcr.microsoft.com/mssql/server:2025-latest` | — | `sqlcmd SELECT 1` |
| Service Bus Emulator | `local-servicebus` | `mcr.microsoft.com/azure-messaging/servicebus-emulator:latest` | 5672, 5300 | HTTP GET `/health` |

All services use `restart: unless-stopped` and named Docker volumes. The Service Bus emulator depends on its internal SQL Server being healthy before starting.

## Environment Configuration

The `.env` file (never committed, git-ignored) holds all secrets. Copy `.env.example` to `.env` and replace placeholders.

| Variable | Purpose | Constraints |
|---|---|---|
| `MSSQL_SA_PASSWORD` | SQL Server SA password | Min 8 chars, upper + lower + digit + special char |
| `MSSQL_PORT` | Host port for SQL Server | Default 1433 |
| `POSTGRES_USER` | PostgreSQL superuser | — |
| `POSTGRES_PASSWORD` | PostgreSQL password | — |
| `POSTGRES_DB` | Default database name | — |
| `POSTGRES_PORT` | Host port for PostgreSQL | Default 5432 |
| `SERVICEBUS_SQL_PASSWORD` | Service Bus internal SQL SA password | Same rules as `MSSQL_SA_PASSWORD` |
| `SERVICEBUS_PORT` | Host port for AMQP | Default 5672 |
| `SERVICEBUS_MGMT_PORT` | Host port for management/health | Default 5300 |

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

## Commands

### Stack Management

```powershell
.\stack.ps1 start             # Start all services, wait for healthy
.\stack.ps1 stop              # Stop containers, preserve volumes
.\stack.ps1 restart           # Full stop/start cycle
.\stack.ps1 nuke              # Destroy containers AND volumes (data loss!)
.\stack.ps1 status            # Show container health/status
.\stack.ps1 logs [service]    # Stream logs (optionally for one service)
.\stack.ps1 pull              # Pull latest images
.\stack.ps1 monitor           # Interactive Service Bus message monitor
```

### Docker Compose

```bash
docker compose up -d          # Start all services
docker compose down           # Stop, preserve volumes
docker compose down -v        # Stop and delete volumes
```

### Building the Monitor Tool

```bash
cd servicebus/monitor
dotnet build
dotnet run
```

## Coding Style & Conventions

### PowerShell (`stack.ps1`)

- **Version**: PowerShell 7.0+ required (`#Requires -Version 7.0`).
- **Strict mode**: `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'`.
- **Terminal output**: ANSI colour codes with automatic fallback for non-ANSI terminals.
- **TUI elements**: Unicode box-drawing characters (`╭╮╰╯│─`) and status symbols (`✓ ✗ ⚠ ▶`).
- **Prerequisite validation**: Always check Docker daemon and `.env` presence before operations.
- **Section headers**: `# ─── Section Name ───` with Unicode line-drawing.
- **Function naming**: `Invoke-<Action>`, `Get-<Property>`, `Assert-<Condition>`, `Wait-<State>`, `Show-<View>`.
- **Error handling**: Catch and report errors with clear, actionable messages.

### C# / .NET (`servicebus/monitor/`)

- **Framework**: .NET 8.0, implicit usings enabled, nullable reference types enabled.
- **Language features**: Records, async/await, `ConcurrentDictionary`, `ConcurrentQueue`.
- **Visual style**: Mirror the PowerShell ANSI colour scheme and TUI styling.
- **Error handling**: Retry logic with exponential backoff (1 s → 30 s cap).
- **Memory safety**: Cap in-memory collections (1000 message limit).
- **Section headers**: `// ─── Section Name ───` matching PowerShell style.

### Docker Compose (`docker-compose.yml`)

- Every service must have a `healthcheck` block with appropriate `interval`, `timeout`, `retries`, and `start_period`.
- Use environment variable substitution from `.env` with defaults: `${VAR:-default}`.
- Never hard-code secrets. Define named volumes for persistent data.
- Add descriptive comment headers for each service section using `# ─── ... ───`.

### JSON (`servicebus/Config.example.json`)

- 2-space indentation.
- Follow the Azure Service Bus Emulator configuration schema exactly.
- `Config.example.json` is the safe-to-commit template; `Config.json` is the user's local copy (git-ignored).

### General Principles

- Comments explain *why*, not *what*.
- Error messages are clear and actionable.
- Degrade gracefully when optional features are unavailable.
- No secrets in source — `.env` is git-ignored.
- Keep `.env.example`, `Config.example.json`, and `README.md` in sync when adding new environment variables or Service Bus entities.

## Important Rules

1. **Health checks are critical** — all Docker services must have one; `stack.ps1` and `depends_on` rely on them.
2. **Never commit `.env` or `servicebus/Config.json`** — both contain local configuration and are git-ignored.
3. **SQL Server password complexity** — min 8 characters, mixed case, digit, and special character required.
4. **Service Bus depends on internal SQL** — `servicebus` starts only after `servicebus-sql` is healthy.
5. **Maintain TUI consistency** — `stack.ps1` and `Program.cs` share a visual style; preserve it.
6. **Volumes persist data** — only `nuke` or `docker compose down -v` destroys volumes.
7. **No CI/CD or test infrastructure** — this is a pure infrastructure repository.
8. **PowerShell 7.0+ only** — do not introduce syntax incompatible with PowerShell 7.

## Troubleshooting

- **Unhealthy containers**: Check password complexity; review logs with `.\stack.ps1 logs <service>`.
- **Port conflicts**: Change ports in `.env` and restart.
- **Startup timeout**: Pull images first (`.\stack.ps1 pull`); check Docker resource allocation.
- **SQL Server connection failures**: Ensure `TrustServerCertificate=True` in connection string.
- **Service Bus not starting**: Check `servicebus-sql` health first.
