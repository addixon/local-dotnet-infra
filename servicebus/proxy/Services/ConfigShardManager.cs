using System.Text.Json;
using ServiceBusProxy.Models;

namespace ServiceBusProxy.Services;

// ─── Config shard manager ────────────────────────────────────────────────────
// Splits a large emulator config (potentially >50 entities) into multiple
// per-emulator configs, each respecting the 50-entity limit.

static class ConfigShardManager
{
    public const int MaxEntitiesPerEmulator = 50;

    /// <summary>
    /// Reads the master config and returns a list of per-emulator configs,
    /// each containing at most <see cref="MaxEntitiesPerEmulator"/> entities.
    /// </summary>
    public static List<EmulatorConfig> Shard(EmulatorConfig masterConfig)
    {
        var ns = masterConfig.UserConfig.Namespaces.FirstOrDefault()
            ?? throw new InvalidOperationException("Config must contain at least one namespace.");

        var allQueues = ns.Queues ?? [];
        var allTopics = ns.Topics ?? [];

        // Combine queues and topics into a flat entity list for sequential-fill sharding
        var entities = new List<(string type, object entity)>();
        foreach (var q in allQueues) entities.Add(("queue", q));
        foreach (var t in allTopics) entities.Add(("topic", t));

        if (entities.Count == 0)
            throw new InvalidOperationException("Config contains no queues or topics.");

        var shards = new List<EmulatorConfig>();
        var shardEntities = new List<(string type, object entity)>();

        foreach (var entity in entities)
        {
            shardEntities.Add(entity);

            if (shardEntities.Count >= MaxEntitiesPerEmulator)
            {
                shards.Add(BuildShardConfig(ns.Name, shardEntities, masterConfig.UserConfig.Logging));
                shardEntities = [];
            }
        }

        // Remaining entities
        if (shardEntities.Count > 0)
            shards.Add(BuildShardConfig(ns.Name, shardEntities, masterConfig.UserConfig.Logging));

        return shards;
    }

    /// <summary>
    /// Returns the entity names (queues + topics) contained in a shard config.
    /// </summary>
    public static List<string> GetEntityNames(EmulatorConfig config)
    {
        var ns = config.UserConfig.Namespaces.FirstOrDefault();
        if (ns is null) return [];

        var names = new List<string>();
        foreach (var q in ns.Queues ?? []) names.Add(q.Name);
        foreach (var t in ns.Topics ?? []) names.Add(t.Name);
        return names;
    }

    /// <summary>
    /// Builds a single-shard emulator config from a list of entities.
    /// </summary>
    public static EmulatorConfig BuildShardConfig(
        string namespaceName,
        IEnumerable<(string type, object entity)> entities,
        LoggingConfig? logging)
    {
        var queues = new List<QueueConfig>();
        var topics = new List<TopicConfig>();

        foreach (var (type, entity) in entities)
        {
            if (type == "queue" && entity is QueueConfig q) queues.Add(q);
            else if (type == "topic" && entity is TopicConfig t) topics.Add(t);
        }

        return new EmulatorConfig(
            new UserConfig(
                [new EmulatorNamespace(namespaceName, queues, topics)],
                logging));
    }

    /// <summary>
    /// Creates a per-emulator config that contains only the specified entity
    /// appended to an existing shard config (for dynamic entity creation).
    /// </summary>
    public static EmulatorConfig AddEntityToConfig(EmulatorConfig existing, string entityName, string entityType)
    {
        var ns = existing.UserConfig.Namespaces.First();
        var queues = new List<QueueConfig>(ns.Queues ?? []);
        var topics = new List<TopicConfig>(ns.Topics ?? []);

        if (entityType == "queue")
            queues.Add(new QueueConfig(entityName));
        else
            topics.Add(new TopicConfig(entityName));

        return new EmulatorConfig(
            new UserConfig(
                [new EmulatorNamespace(ns.Name, queues, topics)],
                existing.UserConfig.Logging));
    }

    /// <summary>
    /// Writes an emulator config to a JSON file.
    /// </summary>
    public static async Task WriteConfigAsync(string path, EmulatorConfig config)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(config, options);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>
    /// Loads an emulator config from a JSON file.
    /// </summary>
    public static async Task<EmulatorConfig> LoadConfigAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<EmulatorConfig>(json)
            ?? throw new InvalidOperationException($"Failed to deserialize config from {path}");
    }
}
