using Docker.DotNet;
using Docker.DotNet.Models;
using ServiceBusProxy.Models;

namespace ServiceBusProxy.Services;

// ─── Emulator orchestrator ───────────────────────────────────────────────────
// Creates, starts, health-checks, and tears down Service Bus Emulator
// containers (and their backing SQL Server containers) via the Docker API.

sealed class EmulatorOrchestrator : IAsyncDisposable
{
    // ── Constants ────────────────────────────────────────────────────────────

    const string EmulatorImage   = "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest";
    const string SqlImage        = "mcr.microsoft.com/mssql/server:2025-latest";
    const string LabelKey        = "managed-by";
    const string LabelValue      = "servicebus-proxy";
    const string NamespaceName   = "sbemulatorns";
    const int    HealthTimeoutSec = 120;
    const int    HealthPollMs     = 2000;

    // Internal AMQP/management base ports (container-internal, not exposed to host)
    const int InternalAmqpPort = 5672;
    const int InternalMgmtPort = 5300;

    // ── Fields ───────────────────────────────────────────────────────────────

    readonly DockerClient     _docker;
    readonly EntityRouter     _router;
    readonly StateManager     _stateManager;
    readonly ILogger          _logger;
    readonly string           _emulatorConfigDir;
    readonly string           _sqlPassword;
    readonly string           _networkName;
    string                    _hostEmulatorConfigDir = ""; // resolved at init
    readonly List<EmulatorInstance> _instances = [];
    readonly SemaphoreSlim    _scaleLock = new(1, 1);

    int _nextAmqpPort;
    int _nextMgmtPort;

    // ── Constructor ──────────────────────────────────────────────────────────

    public EmulatorOrchestrator(
        EntityRouter     router,
        StateManager     stateManager,
        ILogger<EmulatorOrchestrator> logger,
        IConfiguration   config)
    {
        _docker           = new DockerClientConfiguration().CreateClient();
        _router           = router;
        _stateManager     = stateManager;
        _logger           = logger;
        _emulatorConfigDir = config["EMULATOR_CONFIG_DIR"] ?? "/emulator-configs";
        _sqlPassword       = config["SERVICEBUS_SQL_PASSWORD"] ?? "ServiceBus_P@ss1";
        _networkName       = config["DOCKER_NETWORK"] ?? "local-infrastructure_default";

        // Dynamic containers start at these ports (host-side; only the proxy is exposed externally)
        _nextAmqpPort = 15672;
        _nextMgmtPort = 15300;
    }

    // ── Properties ───────────────────────────────────────────────────────────

    public IReadOnlyList<EmulatorInstance> Instances => _instances;

    // ── Initialise from master config ────────────────────────────────────────

    /// <summary>
    /// Read the master config, shard it, and spin up emulators for each shard.
    /// </summary>
    public async Task InitialiseAsync(string masterConfigPath)
    {
        Directory.CreateDirectory(_emulatorConfigDir);
        await EnsureNetworkAsync();
        await ResolveHostConfigDirAsync();

        var masterConfig = await ConfigShardManager.LoadConfigAsync(masterConfigPath);
        var shards = ConfigShardManager.Shard(masterConfig);

        _logger.LogInformation(
            "Config sharded into {Count} emulator(s) ({Entities} total entities)",
            shards.Count,
            shards.Sum(s => ConfigShardManager.GetEntityNames(s).Count));

        for (var i = 0; i < shards.Count; i++)
        {
            var shard = shards[i];
            var instance = await ProvisionShardAsync(i, shard);
            _instances.Add(instance);

            foreach (var name in ConfigShardManager.GetEntityNames(shard))
            {
                _router.Register(name, instance);
                instance.EntityCount++;
            }
        }

        // Persist state
        await SaveStateAsync();

        _logger.LogInformation("All {Count} emulator(s) provisioned and healthy", _instances.Count);
    }

    // ── Dynamic scaling ──────────────────────────────────────────────────────

    /// <summary>
    /// Ensures capacity for a new entity. Returns the emulator instance that
    /// has room. If none do, spins up a new emulator (blocking until healthy).
    /// </summary>
    public async Task<EmulatorInstance> EnsureCapacityAsync(string entityName, string entityType)
    {
        await _scaleLock.WaitAsync();
        try
        {
            // Already routed?
            var existing = _router.GetBackend(entityName);
            if (existing is not null) return existing;

            // Find instance with capacity
            var instance = _router.FindInstanceWithCapacity();
            if (instance is not null)
            {
                _router.Register(entityName, instance);
                instance.EntityCount++;
                await SaveStateAsync();
                return instance;
            }

            // Need a new emulator
            _logger.LogInformation(
                "All emulators at capacity — spinning up shard {Index} for entity '{Entity}'",
                _router.NextShardIndex, entityName);

            var shardIndex = _router.NextShardIndex;
            var shardConfig = ConfigShardManager.BuildShardConfig(
                NamespaceName,
                [(entityType, entityType == "queue"
                    ? (object)new QueueConfig(entityName)
                    : new TopicConfig(entityName))],
                new LoggingConfig("File"));

            var newInstance = await ProvisionShardAsync(shardIndex, shardConfig);
            _instances.Add(newInstance);

            _router.Register(entityName, newInstance);
            newInstance.EntityCount++;

            await SaveStateAsync();

            _logger.LogInformation("Shard {Index} is healthy and ready", shardIndex);
            return newInstance;
        }
        finally
        {
            _scaleLock.Release();
        }
    }

    // ── Provision a shard (SQL + emulator) ───────────────────────────────────

    async Task<EmulatorInstance> ProvisionShardAsync(int shardIndex, EmulatorConfig config)
    {
        var amqpPort = _nextAmqpPort++;
        var mgmtPort = _nextMgmtPort++;

        var sqlName      = $"sbproxy-sql-{shardIndex}";
        var emulatorName = $"sbproxy-emulator-{shardIndex}";

        // Write per-shard config file
        var configPath = Path.Combine(_emulatorConfigDir, $"config-{shardIndex}.json");
        await ConfigShardManager.WriteConfigAsync(configPath, config);

        // Create SQL Server container
        var sqlId = await CreateSqlContainerAsync(sqlName);

        // Wait for SQL to become healthy
        await WaitForContainerHealthyAsync(sqlName, sqlId);

        // Create emulator container
        var emulatorId = await CreateEmulatorContainerAsync(
            emulatorName, sqlName, configPath, amqpPort, mgmtPort);

        // Wait for emulator to become healthy (HTTP /health endpoint)
        await WaitForEmulatorHealthyAsync(emulatorName, mgmtPort);

        return new EmulatorInstance
        {
            ShardIndex       = shardIndex,
            ContainerName    = emulatorName,
            SqlContainerName = sqlName,
            ContainerId      = emulatorId,
            SqlContainerId   = sqlId,
            AmqpPort         = amqpPort,
            MgmtPort         = mgmtPort,
            Healthy          = true
        };
    }

    // ── Docker container creation ────────────────────────────────────────────

    async Task<string> CreateSqlContainerAsync(string name)
    {
        await RemoveContainerIfExistsAsync(name);

        var response = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name  = name,
            Image = SqlImage,
            Env =
            [
                "ACCEPT_EULA=Y",
                "MSSQL_PID=Developer",
                $"SA_PASSWORD={_sqlPassword}"
            ],
            Labels = new Dictionary<string, string>
            {
                [LabelKey] = LabelValue,
                ["sbproxy-role"] = "sql"
            },
            HostConfig = new HostConfig
            {
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                NetworkMode   = _networkName
            },
            Healthcheck = new HealthConfig
            {
                Test        = ["CMD-SHELL", $"/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$SA_PASSWORD\" -C -b -Q \"SELECT 1\" -o /dev/null || exit 1"],
                Interval    = TimeSpan.FromSeconds(10),
                Timeout     = TimeSpan.FromSeconds(5),
                Retries     = 10,
                StartPeriod = (long)TimeSpan.FromSeconds(15).TotalNanoseconds
            }
        });

        await _docker.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
        _logger.LogInformation("Started SQL container {Name} ({Id})", name, response.ID[..12]);
        return response.ID;
    }

    async Task<string> CreateEmulatorContainerAsync(
        string name, string sqlName, string configPath, int amqpPort, int mgmtPort)
    {
        await RemoveContainerIfExistsAsync(name);

        // Compute the HOST-side path for the config file.
        // The proxy container's /emulator-configs is bind-mounted from the host;
        // we resolved the host-side base in ResolveHostConfigDirAsync().
        var configFileName = Path.GetFileName(configPath);
        var hostConfigPath = _hostEmulatorConfigDir + "/" + configFileName;

        var response = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name  = name,
            Image = EmulatorImage,
            Env =
            [
                "ACCEPT_EULA=Y",
                $"SQL_SERVER={sqlName}",
                $"MSSQL_SA_PASSWORD={_sqlPassword}"
            ],
            Labels = new Dictionary<string, string>
            {
                [LabelKey] = LabelValue,
                ["sbproxy-role"]  = "emulator",
                ["sbproxy-shard"] = amqpPort.ToString()
            },
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                [$"{InternalAmqpPort}/tcp"] = default,
                [$"{InternalMgmtPort}/tcp"] = default
            },
            HostConfig = new HostConfig
            {
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                NetworkMode   = _networkName,
                Binds = [$"{hostConfigPath}:/ServiceBus_Emulator/ConfigFiles/Config.json"],
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [$"{InternalAmqpPort}/tcp"] = [new PortBinding { HostPort = amqpPort.ToString() }],
                    [$"{InternalMgmtPort}/tcp"] = [new PortBinding { HostPort = mgmtPort.ToString() }]
                }
            }
        });

        await _docker.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
        _logger.LogInformation("Started emulator container {Name} ({Id}) — AMQP:{Amqp} Mgmt:{Mgmt}",
            name, response.ID[..12], amqpPort, mgmtPort);
        return response.ID;
    }

    // ── Health checking ──────────────────────────────────────────────────────

    async Task WaitForContainerHealthyAsync(string name, string containerId)
    {
        _logger.LogInformation("Waiting for {Name} to become healthy…", name);
        var deadline = DateTime.UtcNow.AddSeconds(HealthTimeoutSec);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var inspect = await _docker.Containers.InspectContainerAsync(containerId);
                var health = inspect.State?.Health?.Status;

                if (health == "healthy")
                {
                    _logger.LogInformation("{Name} is healthy", name);
                    return;
                }

                if (inspect.State?.Status is "exited" or "dead")
                    throw new InvalidOperationException($"Container {name} exited unexpectedly");
            }
            catch (DockerContainerNotFoundException)
            {
                throw new InvalidOperationException($"Container {name} not found");
            }

            await Task.Delay(HealthPollMs);
        }

        throw new TimeoutException($"Container {name} did not become healthy within {HealthTimeoutSec}s");
    }

    async Task WaitForEmulatorHealthyAsync(string name, int mgmtPort)
    {
        // Health check via Docker network (container name + internal port),
        // not localhost + host-mapped port, because we're inside a container.
        var healthUrl = $"http://{name}:{InternalMgmtPort}/health";
        _logger.LogInformation("Waiting for emulator {Name} to become healthy ({Url})…", name, healthUrl);
        var deadline = DateTime.UtcNow.AddSeconds(HealthTimeoutSec);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        // Brief initial delay — the emulator image takes time to initialise
        await Task.Delay(5000);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = await http.GetAsync(healthUrl);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Emulator {Name} is healthy", name);
                    return;
                }
            }
            catch
            {
                // Connection refused, timeout, etc. — keep polling
            }

            await Task.Delay(HealthPollMs);
        }

        throw new TimeoutException($"Emulator {name} did not become healthy within {HealthTimeoutSec}s");
    }

    /// <summary>Check aggregate health of all emulator instances.</summary>
    public async Task<(bool AllHealthy, List<object> Details)> GetAggregateHealthAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var allHealthy = true;

        // Check all emulators in parallel to keep the aggregate response fast
        var tasks = _instances.Select(async inst =>
        {
            bool healthy;
            try
            {
                var resp = await http.GetAsync($"http://{inst.ContainerName}:{InternalMgmtPort}/health");
                healthy = resp.IsSuccessStatusCode;
            }
            catch
            {
                healthy = false;
            }

            inst.Healthy = healthy;
            return (inst, healthy);
        }).ToList();

        var results = await Task.WhenAll(tasks);
        var details = new List<object>();

        foreach (var (inst, healthy) in results)
        {
            if (!healthy) allHealthy = false;

            details.Add(new
            {
                shard      = inst.ShardIndex,
                container  = inst.ContainerName,
                healthy,
                amqpPort   = inst.AmqpPort,
                mgmtPort   = inst.MgmtPort,
                entities   = inst.EntityCount
            });
        }

        return (allHealthy, details);
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    /// <summary>Stop and remove all proxy-managed containers.</summary>
    public async Task TeardownAsync()
    {
        _logger.LogInformation("Tearing down all proxy-managed containers…");
        var filters = new Dictionary<string, IDictionary<string, bool>>
        {
            ["label"] = new Dictionary<string, bool> { [$"{LabelKey}={LabelValue}"] = true }
        };

        var containers = await _docker.Containers.ListContainersAsync(
            new ContainersListParameters { All = true, Filters = filters });

        foreach (var c in containers)
        {
            try
            {
                await _docker.Containers.StopContainerAsync(c.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 5 });
            }
            catch { /* already stopped */ }

            try
            {
                await _docker.Containers.RemoveContainerAsync(c.ID, new ContainerRemoveParameters { Force = true });
                _logger.LogInformation("Removed container {Name}", c.Names.FirstOrDefault());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove container {Id}", c.ID[..12]);
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    async Task RemoveContainerIfExistsAsync(string name)
    {
        try
        {
            var containers = await _docker.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All     = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool> { [name] = true }
                    }
                });

            foreach (var c in containers)
            {
                try { await _docker.Containers.StopContainerAsync(c.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 2 }); }
                catch { /* already stopped */ }
                await _docker.Containers.RemoveContainerAsync(c.ID, new ContainerRemoveParameters { Force = true });
            }
        }
        catch { /* doesn't exist */ }
    }

    async Task EnsureNetworkAsync()
    {
        var networks = await _docker.Networks.ListNetworksAsync(new NetworksListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["name"] = new Dictionary<string, bool> { [_networkName] = true }
            }
        });

        if (networks.Count == 0)
        {
            await _docker.Networks.CreateNetworkAsync(new NetworksCreateParameters { Name = _networkName });
            _logger.LogInformation("Created Docker network: {Name}", _networkName);
        }
    }

    /// <summary>
    /// Inspects the proxy's own container to discover the host-side path
    /// of the /emulator-configs bind mount. This host path is needed when
    /// creating emulator containers, because Docker bind mount sources
    /// are always host paths.
    /// </summary>
    async Task ResolveHostConfigDirAsync()
    {
        try
        {
            // HOSTNAME is set to the container ID by Docker
            var containerId = Environment.GetEnvironmentVariable("HOSTNAME") ?? "";
            if (string.IsNullOrEmpty(containerId))
            {
                _logger.LogWarning("HOSTNAME not set — falling back to container config dir as host path");
                _hostEmulatorConfigDir = _emulatorConfigDir;
                return;
            }

            var inspect = await _docker.Containers.InspectContainerAsync(containerId);
            var mount = inspect.Mounts?.FirstOrDefault(m =>
                m.Destination == _emulatorConfigDir ||
                m.Destination == _emulatorConfigDir.TrimEnd('/'));

            if (mount is not null)
            {
                _hostEmulatorConfigDir = mount.Source;
                _logger.LogInformation(
                    "Resolved host config dir: {ContainerPath} → {HostPath}",
                    _emulatorConfigDir, _hostEmulatorConfigDir);
            }
            else
            {
                _logger.LogWarning(
                    "Could not find mount for {Path} — using container path as fallback",
                    _emulatorConfigDir);
                _hostEmulatorConfigDir = _emulatorConfigDir;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect own container — using container path as fallback");
            _hostEmulatorConfigDir = _emulatorConfigDir;
        }
    }

    async Task SaveStateAsync()
    {
        var state = StateManager.BuildState(_router, _instances);
        await _stateManager.SaveAsync(state);
    }

    public async ValueTask DisposeAsync()
    {
        _scaleLock.Dispose();
        _docker.Dispose();
        await ValueTask.CompletedTask;
    }
}
