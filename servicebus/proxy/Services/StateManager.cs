using System.Text.Json;
using ServiceBusProxy.Models;

namespace ServiceBusProxy.Services;

// ─── State manager ───────────────────────────────────────────────────────────
// Persists proxy routing state to a JSON file so dynamically created emulators
// survive stack restarts. The state file is deleted on stack nuke.

sealed class StateManager(string statePath, ILogger<StateManager> logger)
{
    static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Load proxy state from disk. Returns empty state if file doesn't exist.</summary>
    public ProxyState Load()
    {
        if (!File.Exists(statePath))
        {
            logger.LogInformation("No existing proxy state found at {Path} — starting fresh", statePath);
            return new ProxyState();
        }

        try
        {
            var json = File.ReadAllText(statePath);
            var state = JsonSerializer.Deserialize<ProxyState>(json, _jsonOpts);
            logger.LogInformation("Loaded proxy state from {Path}: {ShardCount} shard(s), {DynCount} dynamic entity(ies)",
                statePath, state?.Shards.Count ?? 0, state?.DynamicEntities.Count ?? 0);
            return state ?? new ProxyState();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load proxy state from {Path} — starting fresh", statePath);
            return new ProxyState();
        }
    }

    /// <summary>Persist current proxy state to disk.</summary>
    public async Task SaveAsync(ProxyState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(statePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(state, _jsonOpts);
            await File.WriteAllTextAsync(statePath, json);
            logger.LogDebug("Proxy state saved to {Path}", statePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save proxy state to {Path}", statePath);
        }
    }

    /// <summary>Build a ProxyState snapshot from the current router and orchestrator state.</summary>
    public static ProxyState BuildState(EntityRouter router, IReadOnlyCollection<EmulatorInstance> instances)
    {
        var shards = instances.Select(inst => new ShardState
        {
            ShardIndex          = inst.ShardIndex,
            EmulatorContainerId = inst.ContainerId,
            SqlContainerId      = inst.SqlContainerId,
            EmulatorAmqpPort    = inst.AmqpPort,
            EmulatorMgmtPort    = inst.MgmtPort,
            EntityNames         = router.AllEntities
                .Where(e => router.GetBackend(e)?.ShardIndex == inst.ShardIndex)
                .ToList()
        }).ToList();

        return new ProxyState { Shards = shards };
    }

    /// <summary>Delete the state file (used during nuke).</summary>
    public void Delete()
    {
        if (File.Exists(statePath))
        {
            File.Delete(statePath);
            logger.LogInformation("Proxy state file deleted: {Path}", statePath);
        }
    }
}
