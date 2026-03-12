using System.Collections.Concurrent;
using ServiceBusProxy.Models;

namespace ServiceBusProxy.Services;

// ─── Entity router ───────────────────────────────────────────────────────────
// Maps entity names (topics/queues) to their hosting emulator instance.

sealed class EntityRouter
{
    readonly ConcurrentDictionary<string, EmulatorInstance> _routes = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<int, EmulatorInstance>    _shards = new();
    readonly object _lock = new();

    // Subscription tracking — keyed by topic name (case-insensitive)
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _subscriptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register an entity → emulator mapping.</summary>
    public void Register(string entityName, EmulatorInstance instance)
    {
        _routes[entityName] = instance;
        _shards[instance.ShardIndex] = instance;
    }

    /// <summary>Register a subscription under a topic.</summary>
    public void RegisterSubscription(string topicName, string subscriptionName)
    {
        var subs = _subscriptions.GetOrAdd(topicName, _ => new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
        subs.TryAdd(subscriptionName, true);
    }

    /// <summary>Get all subscription names for a topic.</summary>
    public IReadOnlyCollection<string> GetSubscriptions(string topicName)
    {
        if (_subscriptions.TryGetValue(topicName, out var subs))
            return subs.Keys.ToList();
        return [];
    }

    /// <summary>Get all topics with their subscriptions.</summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> AllTopicsWithSubscriptions
    {
        get
        {
            var result = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in _routes.Keys)
            {
                result[entity] = GetSubscriptions(entity);
            }
            return result;
        }
    }

    /// <summary>Look up the emulator instance hosting a given entity.</summary>
    public EmulatorInstance? GetBackend(string entityName)
    {
        // Strip subscription path: "topic.1/Subscriptions/sub.1" → "topic.1"
        var baseName = ParseTopLevelEntity(entityName);
        _routes.TryGetValue(baseName, out var instance);
        return instance;
    }

    /// <summary>Check if an entity is already registered.</summary>
    public bool Contains(string entityName) =>
        _routes.ContainsKey(ParseTopLevelEntity(entityName));

    /// <summary>Get all registered entity names.</summary>
    public IReadOnlyCollection<string> AllEntities => _routes.Keys.ToList();

    /// <summary>Get all emulator instances.</summary>
    public IReadOnlyCollection<EmulatorInstance> AllInstances => _shards.Values.ToList();

    /// <summary>Get an emulator instance by shard index.</summary>
    public EmulatorInstance? GetShard(int index)
    {
        _shards.TryGetValue(index, out var instance);
        return instance;
    }

    /// <summary>Find an emulator with capacity for one more entity.</summary>
    public EmulatorInstance? FindInstanceWithCapacity()
    {
        lock (_lock)
        {
            return _shards.Values
                .Where(i => i.EntityCount < ConfigShardManager.MaxEntitiesPerEmulator)
                .OrderBy(i => i.ShardIndex)
                .FirstOrDefault();
        }
    }

    /// <summary>Get the next shard index for a new emulator.</summary>
    public int NextShardIndex => _shards.IsEmpty ? 0 : _shards.Keys.Max() + 1;

    /// <summary>Total number of entities across all emulators.</summary>
    public int TotalEntities => _routes.Count;

    /// <summary>
    /// Extracts the top-level entity name from an AMQP address.
    /// Handles paths like "topicname/Subscriptions/subname" or "$management".
    /// </summary>
    static string ParseTopLevelEntity(string address)
    {
        if (string.IsNullOrEmpty(address)) return address;

        // Special AMQP addresses pass through as-is
        if (address.StartsWith('$')) return address;

        var slashIdx = address.IndexOf('/');
        return slashIdx >= 0 ? address[..slashIdx] : address;
    }
}
