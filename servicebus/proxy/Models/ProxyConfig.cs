using System.Text.Json.Serialization;

namespace ServiceBusProxy.Models;

// ─── Emulator Config.json models (matches Azure Service Bus Emulator schema) ─

sealed record EmulatorConfig(
    [property: JsonPropertyName("UserConfig")] UserConfig UserConfig);

sealed record UserConfig(
    [property: JsonPropertyName("Namespaces")] List<EmulatorNamespace> Namespaces,
    [property: JsonPropertyName("Logging")]    LoggingConfig?          Logging = null);

sealed record EmulatorNamespace(
    [property: JsonPropertyName("Name")]   string              Name,
    [property: JsonPropertyName("Queues")] List<QueueConfig>?  Queues = null,
    [property: JsonPropertyName("Topics")] List<TopicConfig>?  Topics = null);

sealed record QueueConfig(
    [property: JsonPropertyName("Name")]       string                         Name,
    [property: JsonPropertyName("Properties")] Dictionary<string, object>?    Properties = null);

sealed record TopicConfig(
    [property: JsonPropertyName("Name")]          string                         Name,
    [property: JsonPropertyName("Properties")]    Dictionary<string, object>?    Properties = null,
    [property: JsonPropertyName("Subscriptions")] List<SubscriptionConfig>?      Subscriptions = null);

sealed record SubscriptionConfig(
    [property: JsonPropertyName("Name")]       string                         Name,
    [property: JsonPropertyName("Properties")] Dictionary<string, object>?    Properties = null,
    [property: JsonPropertyName("Rules")]      List<RuleConfig>?              Rules = null);

sealed record RuleConfig(
    [property: JsonPropertyName("Name")]       string                         Name,
    [property: JsonPropertyName("Properties")] Dictionary<string, object>?    Properties = null);

sealed record LoggingConfig(
    [property: JsonPropertyName("Type")] string Type);

// ─── Proxy state (persisted across restarts) ─────────────────────────────────

sealed record ProxyState
{
    [JsonPropertyName("shards")]
    public List<ShardState> Shards { get; init; } = [];

    [JsonPropertyName("dynamicEntities")]
    public List<DynamicEntityRecord> DynamicEntities { get; init; } = [];
}

sealed record ShardState
{
    [JsonPropertyName("shardIndex")]
    public int ShardIndex { get; init; }

    [JsonPropertyName("emulatorContainerId")]
    public string? EmulatorContainerId { get; init; }

    [JsonPropertyName("sqlContainerId")]
    public string? SqlContainerId { get; init; }

    [JsonPropertyName("emulatorAmqpPort")]
    public int EmulatorAmqpPort { get; init; }

    [JsonPropertyName("emulatorMgmtPort")]
    public int EmulatorMgmtPort { get; init; }

    [JsonPropertyName("entityNames")]
    public List<string> EntityNames { get; init; } = [];
}

/// <summary>Entities created at runtime via the management API.</summary>
sealed record DynamicEntityRecord
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "topic"; // "topic" or "queue"

    [JsonPropertyName("shardIndex")]
    public int ShardIndex { get; init; }
}

// ─── Internal runtime model ─────────────────────────────────────────────────

sealed class EmulatorInstance
{
    public int    ShardIndex       { get; init; }
    public string ContainerName    { get; set; } = "";
    public string SqlContainerName { get; set; } = "";
    public string ContainerId      { get; set; } = "";
    public string SqlContainerId   { get; set; } = "";
    public int    AmqpPort         { get; set; }
    public int    MgmtPort         { get; set; }
    public int    EntityCount      { get; set; }
    public bool   Healthy          { get; set; }

    /// <summary>Internal Docker network hostname for AMQP (container name).</summary>
    public string AmqpHost => ContainerName;

    /// <summary>Internal Docker network hostname for management HTTP.</summary>
    public string MgmtHost => ContainerName;
}
