#Requires -Version 5.1
<#
.SYNOPSIS
    Manage the local Docker infrastructure stack.

.DESCRIPTION
    Provides commands to start, stop, restart, and nuke the
    local-infrastructure Docker Compose stack, with rich terminal output
    and success verification after each action.

.PARAMETER Action
    The action to perform:
      start    – Bring up all services and wait until every container is healthy
      stop     – Stop and remove containers (volumes are preserved)
      restart  – Full stop/start cycle; waits for healthy state
      nuke     – Destroy all containers AND volumes  ⚠ DATA LOSS
      status   – Print the current state of every container
      logs     – Stream recent logs, optionally filtered to one service
      pull     – Pull latest images without starting the stack
      monitor  – Monitor Service Bus topics for new messages (interactive TUI)

.PARAMETER Service
    For the 'logs' action: limit output to this service name
    (mssql | postgres | servicebus | servicebus-sql).

.EXAMPLE
    .\stack.ps1 start
    .\stack.ps1 stop
    .\stack.ps1 restart
    .\stack.ps1 nuke
    .\stack.ps1 status
    .\stack.ps1 logs
    .\stack.ps1 logs mssql
    .\stack.ps1 pull
    .\stack.ps1 monitor
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'restart', 'nuke', 'status', 'logs', 'pull', 'monitor')]
    [string]$Action,

    [Parameter(Position = 1)]
    [string]$Service = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ─── Constants ────────────────────────────────────────────────────────────────

$Script:ComposeFile = Join-Path $PSScriptRoot 'docker-compose.yml'
$Script:EnvFile     = Join-Path $PSScriptRoot '.env'
$Script:ConfigFile  = Join-Path (Join-Path $PSScriptRoot 'servicebus') 'Config.json'
$Script:Timeout     = 300   # seconds before health-wait gives up (proxy needs time to spin up emulators)

# Disable BuildKit — Rancher Desktop and Podman do not always support it.
# This ensures `docker compose build` uses the classic builder.
$env:DOCKER_BUILDKIT = '0'

# Label used by the proxy to tag dynamically created emulator/SQL containers
$Script:ProxyLabel = 'managed-by=servicebus-proxy'

# Services in start-up dependency order, with container names and health-check style.
# HealthUrl: when set, stack.ps1 checks this HTTP endpoint from the host instead of
#            relying on Docker's built-in HEALTHCHECK (needed when the container image
#            has no shell, e.g. the Service Bus emulator proxy).
$Script:ServiceDefs = @(
    [PSCustomObject]@{ Name = 'mssql';            Container = 'local-mssql';            HasHealth = $true;  HealthUrl = $null;                            InitialDelay = 0 }
    [PSCustomObject]@{ Name = 'postgres';          Container = 'local-postgres';          HasHealth = $true;  HealthUrl = $null;                            InitialDelay = 0 }
    [PSCustomObject]@{ Name = 'servicebus-proxy';  Container = 'local-servicebus-proxy';  HasHealth = $false; HealthUrl = 'http://localhost:{0}/health'; HealthPort = 5300; InitialDelay = 30 }
)

# ─── Terminal colour helpers ───────────────────────────────────────────────────

$ESC = [char]27

# Detect ANSI support: Windows Terminal, VS Code terminal, modern consoles
# $Host.UI.SupportsVirtualTerminal is only available in PowerShell 7+; treat
# its absence as $false so the script works on 5.1 as well.
$Script:UseAnsi = $false
try { $Script:UseAnsi = [bool]$Host.UI.SupportsVirtualTerminal } catch { }
$Script:UseAnsi = $Script:UseAnsi -or
                  ($env:WT_SESSION -ne $null -and $env:WT_SESSION -ne '') -or
                  ($env:TERM_PROGRAM -eq 'vscode') -or
                  ($env:TERM -match 'xterm|vt100|ansi')

function script:c {
    param([string]$Code, [string]$Text)
    if ($Script:UseAnsi) { "${ESC}[${Code}m${Text}${ESC}[0m" } else { $Text }
}

function script:Dim     ([string]$t) { c '2'    $t }
function script:Bold    ([string]$t) { c '1'    $t }
function script:Cyan    ([string]$t) { c '36'   $t }
function script:Green   ([string]$t) { c '32'   $t }
function script:Yellow  ([string]$t) { c '33'   $t }
function script:Red     ([string]$t) { c '31'   $t }
function script:BCyan   ([string]$t) { c '1;36' $t }
function script:BGreen  ([string]$t) { c '1;32' $t }
function script:BRed    ([string]$t) { c '1;31' $t }
function script:BYellow ([string]$t) { c '1;33' $t }
function script:BWhite  ([string]$t) { c '1;97' $t }

# ─── Pretty-print helpers ─────────────────────────────────────────────────────

function Write-Banner {
    #          1         2         3         4         5
    # 12345678901234567890123456789012345678901234567890123
    # ─────────────────────────────────────────────────────  (53 dashes)
    # │    local-infrastructure  ·  Docker stack manager    │  (53 inner chars)
    $bar = '─' * 53
    Write-Host ''
    Write-Host "  $(BCyan "╭${bar}╮")"
    Write-Host "  $(BCyan "│")    $(BWhite "local-infrastructure")$(Dim "  ·  Docker stack manager")    $(BCyan "│")"
    Write-Host "  $(BCyan "╰${bar}╯")"
    Write-Host ''
}

function Write-Header ([string]$Text) {
    Write-Host "  $(BCyan "▶") $(Bold $Text)"
    Write-Host ''
}

function Write-Step    ([string]$Text) { Write-Host "    $(Cyan "·") $Text" }
function Write-Success ([string]$Text) { Write-Host "    $(BGreen "✓") $Text" }
function Write-Warn    ([string]$Text) { Write-Host "    $(BYellow "⚠") $Text" }
function Write-Fail    ([string]$Text) { Write-Host "    $(BRed "✗") $Text" }

# ─── Spinner ──────────────────────────────────────────────────────────────────

$Script:SpinFrames = '⠋','⠙','⠹','⠸','⠼','⠴','⠦','⠧','⠇','⠏'
$Script:SpinIdx    = 0

function script:Spin ([string]$Msg) {
    $f = $Script:SpinFrames[$Script:SpinIdx % $Script:SpinFrames.Count]
    $Script:SpinIdx++
    if ($Script:UseAnsi) {
        # \e[0K = erase from cursor to end of line (prevents leftover characters
        # when the message shrinks, e.g. fewer pending services)
        Write-Host "`r    $(Cyan $f) $Msg${ESC}[0K" -NoNewline
    }
}

function script:SpinClear {
    if ($Script:UseAnsi) { Write-Host "`r${ESC}[2K" -NoNewline }
}

# ─── Docker helpers ───────────────────────────────────────────────────────────

function Invoke-Compose ([string[]]$ComposeArgs) {
    & docker compose --file $Script:ComposeFile @ComposeArgs
    return $LASTEXITCODE
}

function Get-CStatus ([string]$ContainerName) {
    $out = docker inspect --format '{{.State.Status}}' $ContainerName 2>$null
    if ($LASTEXITCODE -ne 0) { return 'absent' }
    return $out.Trim()
}

function Get-CHealth ([string]$ContainerName) {
    $out = docker inspect --format '{{.State.Health.Status}}' $ContainerName 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($out)) { return 'none' }
    $v = $out.Trim()
    if ($v -eq '<no value>') { return 'none' }
    return $v
}

function Get-HostPort ([string]$ContainerName, [int]$ContainerPort) {
    $raw = docker port $ContainerName $ContainerPort 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) { return $null }
    # docker port returns lines like "0.0.0.0:5300" or "[::]:5300"
    $parts = ($raw.Trim() -split "`n")[0] -split ':'
    return $parts[-1]
}

function Test-HealthEndpoint ([string]$Url) {
    <#
    Makes a lightweight HTTP GET from the host to the given URL.
    Returns $true on a 2xx response, $false on any error.
    #>
    try {
        $null = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
        return $true
    } catch {
        return $false
    }
}

# ─── Health-polling loop ──────────────────────────────────────────────────────

function Wait-AllHealthy {
    <#
    Polls each defined container until it reaches its expected state:
      • Containers WITH  a Docker health check     → 'healthy'
      • Containers WITH  a HealthUrl (external)     → HTTP 2xx from the host
      • Containers WITHOUT either                   → 'running'
    Services with an InitialDelay are treated as pending (without probing)
    until the delay has elapsed, avoiding unnecessary health-check traffic
    while the service is still initialising.
    Returns $true on full success, $false on failure or timeout.
    #>
    $startTime     = Get-Date
    $deadline      = $startTime.AddSeconds($Script:Timeout)
    $Script:SpinIdx = 0

    Write-Host ''

    while ((Get-Date) -lt $deadline) {
        $elapsed = ((Get-Date) - $startTime).TotalSeconds
        $pending = [System.Collections.Generic.List[string]]::new()
        $failed  = [System.Collections.Generic.List[string]]::new()

        foreach ($def in $Script:ServiceDefs) {
            # Skip probing services whose initial delay has not yet elapsed
            if ($def.InitialDelay -gt 0 -and $elapsed -lt $def.InitialDelay) {
                $pending.Add($def.Name)
                continue
            }

            $st = Get-CStatus $def.Container

            if ($st -eq 'exited' -or $st -eq 'dead') {
                $failed.Add($def.Name)
                continue
            }

            if ($def.HasHealth) {
                $h = Get-CHealth $def.Container
                switch ($h) {
                    'healthy'   { }
                    'unhealthy' { $failed.Add($def.Name) }
                    default     { $pending.Add($def.Name) }
                }
            } elseif ($def.HealthUrl) {
                # External health check — container has no shell so we probe from the host
                if ($st -ne 'running') {
                    $pending.Add($def.Name)
                } else {
                    $port = Get-HostPort $def.Container $def.HealthPort
                    if (-not $port) { $pending.Add($def.Name); continue }
                    $url = $def.HealthUrl -f $port
                    if (-not (Test-HealthEndpoint $url)) {
                        $pending.Add($def.Name)
                    }
                }
            } else {
                if ($st -ne 'running') { $pending.Add($def.Name) }
            }
        }

        if ($failed.Count -gt 0) {
            SpinClear
            Write-Host ''
            foreach ($n in $failed) {
                Write-Fail "Service '$n' failed to start or is unhealthy."
            }
            Write-Host "    Check logs: $(Cyan ".\stack.ps1 logs")"
            Write-Host ''
            return $false
        }

        if ($pending.Count -eq 0) {
            SpinClear
            return $true
        }

        $remaining = [Math]::Max(0, [int][math]::Ceiling(($deadline - (Get-Date)).TotalSeconds))
        Spin "Waiting for: $(Cyan ($pending -join ', '))  $(Dim "($remaining`s remaining)")"
        Start-Sleep -Milliseconds 500
    }

    SpinClear
    Write-Host ''
    Write-Fail "Timed out after $($Script:Timeout)s waiting for containers to become healthy."
    Write-Host "    Check logs: $(Cyan ".\stack.ps1 logs")"
    Write-Host ''
    return $false
}

# ─── Status table ─────────────────────────────────────────────────────────────

function Show-Status {
    $w = @{ svc = 22; st = 12; hl = 13 }

    Write-Host ''
    Write-Host ("  " +
        (Dim ('SERVICE'.PadRight($w.svc + 1))) +
        (Dim ('STATUS'.PadRight($w.st + 1))) +
        (Dim ('HEALTH'.PadRight($w.hl + 1))) +
        (Dim 'PORTS'))
    Write-Host ("  " + (Dim ('─' * ($w.svc + 1 + $w.st + 1 + $w.hl + 1 + 10))))

    foreach ($def in $Script:ServiceDefs) {
        $st    = Get-CStatus $def.Container
        $stRaw = if ($st -eq 'absent') { 'not started' } else { $st }
        $hlRaw = if ($def.HasHealth) {
            Get-CHealth $def.Container
        } elseif ($def.HealthUrl -and $st -eq 'running') {
            $port = Get-HostPort $def.Container $def.HealthPort
            if ($port -and (Test-HealthEndpoint ($def.HealthUrl -f $port))) { 'healthy' } else { 'starting' }
        } else { '-' }
        if ($hlRaw -eq 'none') { $hlRaw = '-' }

        $ports = ''
        if ($st -ne 'absent') {
            $raw = docker inspect --format '{{range .NetworkSettings.Ports}}{{range .}}{{.HostPort}} {{end}}{{end}}' $def.Container 2>$null
            if ($raw) {
                $ports = ($raw.Trim() -split '\s+' | Where-Object { $_ }) -join ', '
            }
        }

        # Pad raw strings first so ANSI codes don't affect column alignment
        $svcCol = $def.Name.PadRight($w.svc)
        $stCol  = $stRaw.PadRight($w.st)
        $hlCol  = $hlRaw.PadRight($w.hl)

        $stC = switch ($stRaw) {
            'running'     { Green  $stCol }
            'not started' { Dim    $stCol }
            'exited'      { Yellow $stCol }
            'dead'        { BRed   $stCol }
            default       { BWhite $stCol }
        }
        $hlC = switch ($hlRaw) {
            'healthy'   { BGreen $hlCol }
            'unhealthy' { BRed   $hlCol }
            'starting'  { Cyan   $hlCol }
            default     { Dim    $hlCol }
        }

        Write-Host "  $svcCol $stC $hlC $ports"
    }

    # Show proxy-managed emulator containers (dynamic)
    $proxyContainers = docker ps -a --filter "label=$($Script:ProxyLabel)" --format '{{.Names}}|{{.State}}|{{.Ports}}' 2>$null
    if ($proxyContainers) {
        Write-Host ("  " + (Dim ('─' * ($w.svc + 1 + $w.st + 1 + $w.hl + 1 + 10))))
        Write-Host "  $(Dim 'Proxy-managed emulator instances:')"
        $lines = @($proxyContainers -split "`n" | Where-Object { $_ })
        foreach ($line in $lines) {
            $parts = $line -split '\|'
            $cName  = if ($parts.Count -gt 0) { $parts[0] } else { '?' }
            $cState = if ($parts.Count -gt 1) { $parts[1] } else { '?' }
            $cPorts = if ($parts.Count -gt 2) { $parts[2] } else { '' }

            $svcCol = ("  " + $cName).PadRight($w.svc)
            $stCol  = $cState.PadRight($w.st)
            $stC = switch ($cState) {
                'running' { Green  $stCol }
                'exited'  { Yellow $stCol }
                'dead'    { BRed   $stCol }
                default   { Dim    $stCol }
            }
            Write-Host "  $svcCol $stC $(Dim '-'.PadRight($w.hl)) $cPorts"
        }
    }

    Write-Host ''
}

# ─── Prerequisite checks ──────────────────────────────────────────────────────

function Assert-Docker {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Write-Fail 'docker is not on your PATH.'
        Write-Host ''
        Write-Host '    Install a license-free Docker runtime with WinGet:'
        Write-Host "      $(Cyan "winget install SUSE.RancherDesktop")"
        Write-Host ''
        exit 1
    }
    docker info *>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Fail 'Docker daemon is not running or is not reachable.'
        Write-Host '    Start Rancher Desktop (or your Docker runtime) and try again.'
        Write-Host ''
        exit 1
    }
}

function Assert-EnvFile {
    if (-not (Test-Path $Script:EnvFile)) {
        Write-Warn ".env file not found."
        Write-Host ''
        Write-Host '    Copy the example file and fill in your passwords:'
        Write-Host "      $(Cyan "Copy-Item .env.example .env")"
        Write-Host "    Then open $(Cyan ".env") and replace every placeholder value."
        Write-Host ''
        exit 1
    }
}

function Assert-ConfigFile {
    if (-not (Test-Path $Script:ConfigFile)) {
        Write-Warn "servicebus/Config.json not found."
        Write-Host ''
        Write-Host '    Copy the example file and customise your queues/topics:'
        Write-Host "      $(Cyan "Copy-Item servicebus/Config.example.json servicebus/Config.json")"
        Write-Host ''
        exit 1
    }

    # Ensure the emulator-configs directory exists (bind mount target)
    $configDir = Join-Path (Join-Path $PSScriptRoot 'servicebus') 'emulator-configs'
    if (-not (Test-Path $configDir)) {
        $null = New-Item -ItemType Directory -Path $configDir -Force
    }
}

function Assert-ProxyCert {
    # Generates a self-signed TLS certificate for the HTTPS admin endpoint
    # (port 443) and imports it into the current user's trusted root store so
    # applications (e.g. MassTransit's ServiceBusAdministrationClient) can
    # connect without TLS errors.  The cert is reused across restarts.
    $certDir = Join-Path (Join-Path $PSScriptRoot 'servicebus') 'proxy-cert'
    $pfxPath = Join-Path $certDir 'localhost.pfx'
    $cerPath = Join-Path $certDir 'localhost.cer'

    if (-not (Test-Path $certDir)) {
        $null = New-Item -ItemType Directory -Path $certDir -Force
    }

    if (Test-Path $pfxPath) { return }

    Write-Step 'Generating TLS certificate for HTTPS admin endpoint…'

    # Generate a self-signed cert valid for 5 years
    $cert = New-SelfSignedCertificate `
        -DnsName 'localhost' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(5) `
        -FriendlyName 'ServiceBus Emulator Proxy (localhost)' `
        -KeyExportPolicy Exportable `
        -KeyLength 2048 `
        -HashAlgorithm SHA256

    # Export PFX (with private key) for the proxy container
    $pw = ConvertTo-SecureString -String 'dev' -Force -AsPlainText
    $null = Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pw

    # Export public cert for trust import
    $null = Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT

    # Remove from the personal store (it was only needed for export)
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue

    # Import into the current user's trusted root store
    Write-Step 'Importing certificate into trusted root store (you may see a confirmation dialog)…'
    $null = Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\Root'
    Write-Success 'TLS certificate created and trusted.'
}

# ─── Actions ──────────────────────────────────────────────────────────────────

function Invoke-Start {
    Write-Header 'Starting stack'

    Write-Step 'Building proxy image…'
    Write-Host ''
    $null = Invoke-Compose 'build'
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Fail 'Failed to build proxy image.'
        exit 1
    }
    Write-Host ''

    Write-Step 'Bringing up services (docker compose up -d)…'
    Write-Host ''
    # Capture all output (docker compose sends progress to stderr) and filter out
    # structured-log warnings like: time="..." level=warning msg="No services to build"
    $composeOutput = & docker compose --file $Script:ComposeFile up -d --remove-orphans 2>&1
    $rc = $LASTEXITCODE
    foreach ($line in $composeOutput) {
        $text = "$line"
        if ($text -match '^time=.*level=') { continue }
        Write-Host $text
    }
    if ($rc -ne 0) {
        Write-Host ''
        Write-Fail "docker compose up failed (exit code $rc)."
        exit 1
    }
    Write-Host ''
    Write-Step 'Waiting for all services to become healthy…'
    if (-not (Wait-AllHealthy)) { exit 1 }
    Show-Status
    Write-Success 'Stack is up and all services are healthy.'
    Write-Host ''
}

function Invoke-Stop {
    Write-Header 'Stopping stack'

    # Stop proxy-managed emulator/SQL containers first
    Write-Step 'Stopping proxy-managed emulator containers…'
    $proxyContainers = docker ps -a --filter "label=$($Script:ProxyLabel)" --format '{{.ID}}' 2>$null
    if ($proxyContainers) {
        $ids = @($proxyContainers -split "`n" | Where-Object { $_ })
        foreach ($id in $ids) {
            docker stop $id *>$null
            docker rm -f $id *>$null
        }
        Write-Success "Stopped $($ids.Count) proxy-managed container(s)."
    }

    Write-Step 'Running docker compose down…'
    Write-Host ''
    $rc = Invoke-Compose 'down'
    if ($rc -ne 0) {
        Write-Host ''
        Write-Fail "docker compose down failed (exit code $rc)."
        exit 1
    }
    Write-Host ''
    Write-Step 'Verifying containers are removed…'
    $still = @($Script:ServiceDefs | Where-Object { (Get-CStatus $_.Container) -ne 'absent' })
    if ($still.Count -gt 0) {
        $names = ($still | ForEach-Object { $_.Name }) -join ', '
        Write-Fail "Container(s) still present: $names"
        exit 1
    }
    Write-Success 'Stack stopped. Data volumes are preserved.'
    Write-Host ''
}

function Invoke-Restart {
    Write-Header 'Restarting stack'

    # Stop proxy-managed containers first
    Write-Step 'Stopping proxy-managed emulator containers…'
    $proxyContainers = docker ps -a --filter "label=$($Script:ProxyLabel)" --format '{{.ID}}' 2>$null
    if ($proxyContainers) {
        $ids = @($proxyContainers -split "`n" | Where-Object { $_ })
        foreach ($id in $ids) {
            docker stop $id *>$null
            docker rm -f $id *>$null
        }
        Write-Success "Stopped $($ids.Count) proxy-managed container(s)."
    }

    Write-Step 'Stopping containers…'
    Write-Host ''
    $rc = Invoke-Compose 'down'
    if ($rc -ne 0) {
        Write-Host ''
        Write-Fail "docker compose down failed (exit code $rc)."
        exit 1
    }
    Write-Host ''
    Write-Step 'Building proxy image…'
    Write-Host ''
    $null = Invoke-Compose 'build'
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Fail 'Failed to build proxy image.'
        exit 1
    }
    Write-Host ''

    Write-Step 'Starting containers…'
    Write-Host ''
    $composeOutput = & docker compose --file $Script:ComposeFile up -d --remove-orphans 2>&1
    $rc = $LASTEXITCODE
    foreach ($line in $composeOutput) {
        $text = "$line"
        if ($text -match '^time=.*level=') { continue }
        Write-Host $text
    }
    if ($rc -ne 0) {
        Write-Host ''
        Write-Fail "docker compose up failed (exit code $rc)."
        exit 1
    }
    Write-Host ''
    Write-Step 'Waiting for all services to become healthy…'
    if (-not (Wait-AllHealthy)) { exit 1 }
    Show-Status
    Write-Success 'Stack restarted and all services are healthy.'
    Write-Host ''
}

function Invoke-Nuke {
    Write-Header 'Nuke stack'
    Write-Host "  $(BRed "  ⚠  WARNING")"
    Write-Host "  $(Red "     This action permanently deletes all container volumes.")"
    Write-Host "  $(Red "     All database data (SQL Server, PostgreSQL) will be lost.")"
    Write-Host "  $(Red "     All dynamically created Service Bus emulators will be removed.")"
    Write-Host ''
    Write-Host "  $(BYellow "Type 'YES' to confirm, or press Enter to abort.")"
    $answer = Read-Host '  >'
    if ($answer -ne 'YES') {
        Write-Host ''
        Write-Warn 'Aborted — no changes were made.'
        Write-Host ''
        exit 0
    }
    Write-Host ''

    # Remove dynamically created emulator/SQL containers (managed by the proxy)
    Write-Step 'Removing proxy-managed emulator containers…'
    $proxyContainers = docker ps -a --filter "label=$($Script:ProxyLabel)" --format '{{.ID}}' 2>$null
    if ($proxyContainers) {
        $ids = @($proxyContainers -split "`n" | Where-Object { $_ })
        foreach ($id in $ids) {
            docker stop $id *>$null
            docker rm -f $id *>$null
        }
        Write-Success "Removed $($ids.Count) proxy-managed container(s)."
    } else {
        Write-Step 'No proxy-managed containers found.'
    }
    Write-Host ''

    Write-Step 'Running docker compose down -v…'
    Write-Host ''
    $rc = Invoke-Compose 'down', '-v'
    if ($rc -ne 0) {
        Write-Host ''
        Write-Fail "docker compose down -v failed (exit code $rc)."
        exit 1
    }
    Write-Host ''

    # Clean up emulator config shards and proxy state
    $emulatorConfigDir = Join-Path (Join-Path $PSScriptRoot 'servicebus') 'emulator-configs'
    if (Test-Path $emulatorConfigDir) {
        Remove-Item -Path $emulatorConfigDir -Recurse -Force
        Write-Step 'Removed emulator config shards.'
    }

    Write-Step 'Verifying containers are removed…'
    $still = @($Script:ServiceDefs | Where-Object { (Get-CStatus $_.Container) -ne 'absent' })
    if ($still.Count -gt 0) {
        $names = ($still | ForEach-Object { $_.Name }) -join ', '
        Write-Fail "Container(s) still present: $names"
        exit 1
    }
    Write-Success 'Stack nuked — all containers, volumes, and proxy state have been removed.'
    Write-Host ''
}

function Invoke-Status {
    Write-Header 'Stack status'
    Show-Status
}

function Invoke-Logs ([string]$SvcFilter) {
    $validNames = $Script:ServiceDefs | ForEach-Object { $_.Name }
    if ($SvcFilter) {
        if ($SvcFilter -notin $validNames) {
            Write-Fail "Unknown service '$SvcFilter'."
            Write-Host "    Valid services: $(Cyan ($validNames -join ", "))"
            Write-Host ''
            exit 1
        }
        Write-Header "Logs — $SvcFilter  (Ctrl+C to stop)"
        Invoke-Compose 'logs', '--tail=100', '-f', $SvcFilter
    } else {
        Write-Header 'Logs — all services  (Ctrl+C to stop)'
        Invoke-Compose 'logs', '--tail=50', '-f'
    }
}

function Invoke-Pull {
    Write-Header 'Pulling latest images'
    Write-Step 'Pulling images — this may take a while…'
    Write-Host ''
    $rc = Invoke-Compose 'pull'
    if ($rc -ne 0) {
        Write-Host ''
        Write-Fail "docker compose pull failed (exit code $rc)."
        exit 1
    }
    Write-Host ''
    Write-Success 'All images are up to date.'
    Write-Host ''
}

# ─── Monitor ──────────────────────────────────────────────────────────────

function Invoke-Monitor {
    Write-Header 'Service Bus Monitor '

    # Ensure dotnet SDK is available
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Fail 'dotnet SDK is not on your PATH.'
        Write-Host "    The monitor requires the .NET SDK (8.0+)."
        Write-Host "    Install it from: $(Cyan 'https://dot.net/download')"
        Write-Host ''
        exit 1
    }

    # Ensure the stack (and Service Bus proxy) is running and healthy
    $sbStatus = Get-CStatus 'local-servicebus-proxy'
    $sbDef    = $Script:ServiceDefs | Where-Object { $_.Name -eq 'servicebus-proxy' }
    $sbReady  = $false
    if ($sbStatus -eq 'running' -and $sbDef.HealthUrl) {
        $port = Get-HostPort 'local-servicebus-proxy' $sbDef.HealthPort
        if ($port) { $sbReady = Test-HealthEndpoint ($sbDef.HealthUrl -f $port) }
    }
    if (-not $sbReady) {
        Write-Step 'Service Bus proxy is not running – starting the stack…'
        Write-Host ''
        $rc = Invoke-Compose 'up', '-d', '--remove-orphans'
        if ($rc -ne 0) {
            Write-Host ''
            Write-Fail "docker compose up failed (exit code $rc)."
            exit 1
        }
        Write-Host ''
        Write-Step 'Waiting for all services to become healthy…'
        if (-not (Wait-AllHealthy)) { exit 1 }
        Write-Success 'Stack is up and all services are healthy.'
        Write-Host ''
    }

    $monitorProject = Join-Path (Join-Path $PSScriptRoot 'servicebus') 'monitor'

    Write-Step 'Building monitor tool (first run may take a moment)…'
    $buildOutput = & dotnet build $monitorProject -c Release --nologo -v quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Fail 'Failed to build monitor tool.'
        $buildOutput | ForEach-Object { Write-Host "    $_" }
        Write-Host ''
        exit 1
    }
    Write-Success 'Monitor tool ready.'
    Write-Host ''

    # The monitor reads Config.json for topic discovery and connects
    # to the proxy's AMQP endpoint (same connection string as direct emulator)
    & dotnet run --project $monitorProject -c Release --no-build -- $Script:ConfigFile
}

# ─── Usage ────────────────────────────────────────────────────────────────────

function Write-Usage {
    Write-Host "  Usage:  $(Cyan ".\stack.ps1") $(Yellow "<action>") $(Dim "[service]")"
    Write-Host ''
    Write-Host '  Actions:'
    $acts = @(
        @{ A = 'start';   D = 'Start all services and wait until healthy'          }
        @{ A = 'stop';    D = 'Stop and remove containers (volumes preserved)'     }
        @{ A = 'restart'; D = 'Full stop/start cycle; waits for healthy state'     }
        @{ A = 'nuke';    D = "Destroy containers AND volumes  $(BRed "⚠ data loss")" }
        @{ A = 'status';  D = 'Show current state of every container'              }
        @{ A = 'logs';    D = 'Stream logs (optionally filtered to one service)'   }
        @{ A = 'pull';    D = 'Pull latest images without starting'                }
        @{ A = 'monitor'; D = 'Monitor Service Bus topics for new messages'        }
    )
    foreach ($a in $acts) {
        Write-Host "    $(Yellow ($a.A.PadRight(12))) $(Dim $a.D)"
    }
    Write-Host ''
}

# ─── Entry point ──────────────────────────────────────────────────────────────

Write-Banner

if (-not $Action) {
    Write-Usage
    exit 0
}

Assert-Docker

switch ($Action.ToLower()) {
    'start'   { Assert-EnvFile; Assert-ConfigFile; Assert-ProxyCert; Invoke-Start   }
    'stop'    {                                                      Invoke-Stop    }
    'restart' { Assert-EnvFile; Assert-ConfigFile; Assert-ProxyCert; Invoke-Restart }
    'nuke'    {                                    Invoke-Nuke               }
    'status'  {                                    Invoke-Status             }
    'logs'    {                                    Invoke-Logs $Service      }
    'pull'    {                                    Invoke-Pull               }
    'monitor' { Assert-EnvFile; Assert-ConfigFile; Invoke-Monitor            }
}
