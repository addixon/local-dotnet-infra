using System.Net;
using System.Text;
using System.Xml.Linq;
using ServiceBusProxy.Models;

namespace ServiceBusProxy.Services;

// ─── HTTP management proxy ───────────────────────────────────────────────────
// Proxies HTTP management requests (Atom XML CRUD used by
// ServiceBusAdministrationClient) and the /health endpoint to the appropriate
// backend emulator(s). List operations are aggregated across all emulators.

sealed class ManagementProxy
{
    readonly EntityRouter          _router;
    readonly EmulatorOrchestrator  _orchestrator;
    readonly ILogger               _logger;
    readonly IHttpClientFactory    _httpFactory;

    // Atom XML namespace
    static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    static readonly XNamespace SbNs = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";

    public ManagementProxy(
        EntityRouter          router,
        EmulatorOrchestrator  orchestrator,
        ILogger<ManagementProxy> logger,
        IHttpClientFactory    httpFactory)
    {
        _router       = router;
        _orchestrator = orchestrator;
        _logger       = logger;
        _httpFactory   = httpFactory;
    }

    // ── Route an incoming management request ─────────────────────────────────

    public async Task HandleRequestAsync(HttpContext context)
    {
        var path   = context.Request.Path.Value ?? "/";
        var method = context.Request.Method;

        _logger.LogDebug("Management request: {Method} {Path}", method, path);

        try
        {
            // Health endpoint — aggregate from all emulators
            if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                await HandleHealthAsync(context);
                return;
            }

            // Proxy status endpoint — detailed emulator info
            if (path.Equals("/_proxy/status", StringComparison.OrdinalIgnoreCase))
            {
                await HandleProxyStatusAsync(context);
                return;
            }

            // List all topics: GET /$Resources/Topics
            if (path.StartsWith("/$Resources/Topics", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                await HandleAggregateListAsync(context, path);
                return;
            }

            // List all queues: GET /$Resources/Queues
            if (path.StartsWith("/$Resources/Queues", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                await HandleAggregateListAsync(context, path);
                return;
            }

            // Entity-specific operations
            await HandleEntityRequestAsync(context, path, method);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Management proxy error for {Method} {Path}", method, path);
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"Proxy error: {ex.Message}");
        }
    }

    // ── Health aggregation ───────────────────────────────────────────────────

    async Task HandleHealthAsync(HttpContext context)
    {
        var (allHealthy, details) = await _orchestrator.GetAggregateHealthAsync();

        context.Response.StatusCode = allHealthy ? 200 : 503;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status    = allHealthy ? "healthy" : "degraded",
            emulators = details
        });
    }

    // ── Proxy status (detailed emulator info) ────────────────────────────────

    async Task HandleProxyStatusAsync(HttpContext context)
    {
        var (allHealthy, details) = await _orchestrator.GetAggregateHealthAsync();

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status         = allHealthy ? "healthy" : "degraded",
            totalEntities  = _router.TotalEntities,
            emulatorCount  = _orchestrator.Instances.Count,
            maxPerEmulator = ConfigShardManager.MaxEntitiesPerEmulator,
            emulators      = details
        });
    }

    // ── Aggregate list operations ────────────────────────────────────────────

    async Task HandleAggregateListAsync(HttpContext context, string path)
    {
        // Send the list request to every backend emulator and merge Atom feeds
        var entries = new List<XElement>();
        var client  = _httpFactory.CreateClient("management");

        foreach (var instance in _orchestrator.Instances)
        {
            try
            {
                var url  = $"http://{instance.ContainerName}:5300{path}";
                var resp = await client.GetAsync(url);

                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    try
                    {
                        var doc = XDocument.Parse(body);
                        var feed = doc.Root;
                        if (feed is not null)
                        {
                            entries.AddRange(feed.Elements(Atom + "entry"));
                        }
                    }
                    catch
                    {
                        // Non-XML response — skip
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list from shard {Shard}", instance.ShardIndex);
            }
        }

        // Build a merged Atom feed
        var merged = new XDocument(
            new XElement(Atom + "feed",
                new XElement(Atom + "title", "Aggregated"),
                entries));

        var xml = merged.ToString();
        context.Response.ContentType = "application/atom+xml;type=feed;charset=utf-8";
        context.Response.StatusCode = 200;
        await context.Response.WriteAsync(xml);
    }

    // ── Entity-specific requests ─────────────────────────────────────────────

    async Task HandleEntityRequestAsync(HttpContext context, string path, string method)
    {
        // Parse entity name from path (first segment after /)
        var entityName = ParseEntityNameFromPath(path);

        if (string.IsNullOrEmpty(entityName))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Could not determine entity name from path");
            return;
        }

        var instance = _router.GetBackend(entityName);

        // CREATE (PUT): if entity doesn't exist, route to emulator with capacity
        if (instance is null && method == "PUT")
        {
            _logger.LogInformation("Dynamic entity creation: '{Entity}' via management API", entityName);

            // Determine entity type from path
            var entityType = DetermineEntityType(path);
            instance = await _orchestrator.EnsureCapacityAsync(entityName, entityType);
        }

        if (instance is null)
        {
            // For subscription operations on existing topics, route to the topic's emulator
            var parentEntity = ParseParentEntity(path);
            if (parentEntity is not null)
                instance = _router.GetBackend(parentEntity);
        }

        if (instance is null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync($"Entity '{entityName}' not found");
            return;
        }

        // Forward the request to the backend
        await ForwardRequestAsync(context, instance, path);
    }

    // ── HTTP forwarding ──────────────────────────────────────────────────────

    async Task ForwardRequestAsync(HttpContext context, EmulatorInstance instance, string path)
    {
        var client = _httpFactory.CreateClient("management");
        var url    = $"http://{instance.ContainerName}:5300{path}";

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), url);

        // Forward headers
        foreach (var header in context.Request.Headers)
        {
            if (!header.Key.StartsWith("Host", StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        // Forward body
        if (context.Request.ContentLength > 0 || context.Request.ContentType is not null)
        {
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            request.Content = new StringContent(body, Encoding.UTF8,
                context.Request.ContentType ?? "application/xml");
        }

        var response = await client.SendAsync(request);

        // Copy response
        context.Response.StatusCode = (int)response.StatusCode;

        foreach (var header in response.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();

        foreach (var header in response.Content.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();

        // Don't duplicate content-length
        context.Response.Headers.Remove("Transfer-Encoding");

        var responseBody = await response.Content.ReadAsStringAsync();
        await context.Response.WriteAsync(responseBody);
    }

    // ── Path parsing helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Parse the top-level entity name from a management API path.
    /// Examples:
    ///   /my-topic                         → my-topic
    ///   /my-topic/Subscriptions/sub-1     → my-topic
    ///   /my-queue                         → my-queue
    ///   /$Resources/Topics                → (empty)
    /// </summary>
    static string? ParseEntityNameFromPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return null;
        if (path.StartsWith("/$")) return null;

        var trimmed = path.TrimStart('/');
        var slashIdx = trimmed.IndexOf('/');
        return slashIdx >= 0 ? trimmed[..slashIdx] : trimmed;
    }

    /// <summary>
    /// For subscription/rule operations, extract the parent topic name.
    /// /my-topic/Subscriptions/sub-1 → my-topic
    /// </summary>
    static string? ParseParentEntity(string path)
    {
        var trimmed = path.TrimStart('/');
        var parts = trimmed.Split('/');
        if (parts.Length >= 2 && parts[1].Equals("Subscriptions", StringComparison.OrdinalIgnoreCase))
            return parts[0];
        return null;
    }

    /// <summary>
    /// Determine entity type from the management API path.
    /// Paths containing /Subscriptions/ imply the parent is a topic.
    /// Paths matching /$Resources/Queues imply queue.
    /// Default: topic.
    /// </summary>
    static string DetermineEntityType(string path)
    {
        if (path.Contains("Subscriptions", StringComparison.OrdinalIgnoreCase)) return "topic";
        if (path.Contains("Queue", StringComparison.OrdinalIgnoreCase)) return "queue";
        return "topic";
    }
}
