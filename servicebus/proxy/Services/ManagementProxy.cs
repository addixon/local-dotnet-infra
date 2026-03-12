using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Azure.Messaging.ServiceBus.Administration;
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

            // Entity registry — topics with their subscriptions (for the monitor)
            if (path.Equals("/_proxy/entities", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEntitiesAsync(context);
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

    // ── Entity registry (for monitor discovery) ──────────────────────────────

    async Task HandleEntitiesAsync(HttpContext context)
    {
        var topics = _router.AllTopicsWithSubscriptions;
        var result = topics.Select(kvp => new
        {
            topic         = kvp.Key,
            subscriptions = kvp.Value
        });

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(result);
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
                var url  = $"http://{instance.ContainerName}:5300{path}{context.Request.QueryString}";
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

        // Detect top-level topic/queue creation so we can create the monitor
        // subscription BEFORE the client sees the 201 (avoids a race where the
        // client publishes a message before the subscription exists).
        var isTopLevelCreation = method == "PUT"
            && ParseSubscriptionFromPath(path) is null
            && ParseParentEntity(path) is null;

        if (isTopLevelCreation)
        {
            var forwarded = await ForwardToBackendAsync(context, instance, path);

            if (forwarded.StatusCode is 200 or 201)
            {
                // Auto-create 'monitor' subscription on the backend emulator
                // before the client receives the response.
                await CreateMonitorSubscriptionAsync(instance, entityName);
            }

            await WriteForwardedResponseAsync(context, forwarded);
        }
        else
        {
            await ForwardRequestAsync(context, instance, path);

            if (method == "PUT" && context.Response.StatusCode is 200 or 201)
            {
                var subInfo = ParseSubscriptionFromPath(path);
                if (subInfo is not null)
                {
                    // Track subscription creation for discovery (emulator's
                    // subscription listing endpoint is broken, so the proxy
                    // maintains its own registry)
                    _router.RegisterSubscription(subInfo.Value.topic, subInfo.Value.subscription);
                    _logger.LogInformation("Registered subscription '{Topic}/{Sub}'",
                        subInfo.Value.topic, subInfo.Value.subscription);
                }
            }
        }
    }

    // ── Auto-create monitor subscription ─────────────────────────────────────

    const string MonitorSubscriptionName = "monitor";

    /// <summary>
    /// Creates the 'monitor' subscription via the ServiceBusAdministrationClient
    /// SDK.  The SDK produces the required SAS auth headers; the request flows
    /// SDK → proxy HTTPS:443 → emulator :5300.
    ///
    /// Called from the buffered topic-creation path so the subscription exists
    /// BEFORE the 201 is returned to the client (prevents the race where the
    /// client publishes a message before the subscription exists).
    /// </summary>
    async Task CreateMonitorSubscriptionAsync(EmulatorInstance instance, string topicName)
    {
        try
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            var options = new ServiceBusAdministrationClientOptions();
            options.Transport = new Azure.Core.Pipeline.HttpClientTransport(handler);

            // Omit UseDevelopmentEmulator so the SDK connects via HTTPS:443
            // (the proxy's own listener) instead of HTTP:80.
            var connStr =
                "Endpoint=sb://localhost;" +
                "SharedAccessKeyName=RootManageSharedAccessKey;" +
                "SharedAccessKey=SAS_KEY_VALUE;";

            var adminClient = new ServiceBusAdministrationClient(connStr, options);
            await adminClient.CreateSubscriptionAsync(
                new CreateSubscriptionOptions(topicName, MonitorSubscriptionName)
                {
                    LockDuration             = TimeSpan.FromSeconds(30),
                    DefaultMessageTimeToLive = TimeSpan.FromHours(1),
                    MaxDeliveryCount         = 1
                });

            _router.RegisterSubscription(topicName, MonitorSubscriptionName);
            _logger.LogInformation("Auto-created '{Sub}' subscription on '{Topic}'",
                MonitorSubscriptionName, topicName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error auto-creating '{Sub}' subscription on '{Topic}'",
                MonitorSubscriptionName, topicName);
        }
    }

    // ── HTTP forwarding ──────────────────────────────────────────────────────

    /// <summary>Buffered response from a backend emulator.</summary>
    sealed record ForwardedResponse(
        int StatusCode,
        List<KeyValuePair<string, string[]>> Headers,
        string Body);

    /// <summary>
    /// Sends the incoming request to the backend emulator and returns the
    /// response without writing it to the client.  Callers can inspect
    /// the result (e.g. to create a monitor subscription on 201) before
    /// flushing via <see cref="WriteForwardedResponseAsync"/>.
    /// </summary>
    async Task<ForwardedResponse> ForwardToBackendAsync(
        HttpContext context, EmulatorInstance instance, string path)
    {
        var client = _httpFactory.CreateClient("management");
        var url    = $"http://{instance.ContainerName}:5300{path}{context.Request.QueryString}";

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

            // PUT = create/update entity — clamp timespan properties to emulator limits
            if (context.Request.Method == "PUT")
                body = ClampTimespanProperties(body);

            // Use ByteArrayContent to avoid StringContent rejecting complex
            // media types like "application/atom+xml;type=entry;charset=utf-8"
            var bytes = Encoding.UTF8.GetBytes(body);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.TryAddWithoutValidation("Content-Type",
                context.Request.ContentType ?? "application/xml");
        }

        var response = await client.SendAsync(request);

        // Collect all response headers into a flat list
        var headers = new List<KeyValuePair<string, string[]>>();
        foreach (var h in response.Headers)
            headers.Add(new(h.Key, h.Value.ToArray()));
        foreach (var h in response.Content.Headers)
            headers.Add(new(h.Key, h.Value.ToArray()));

        var responseBody = await response.Content.ReadAsStringAsync();
        return new ForwardedResponse((int)response.StatusCode, headers, responseBody);
    }

    /// <summary>Write a buffered <see cref="ForwardedResponse"/> to the client.</summary>
    async Task WriteForwardedResponseAsync(HttpContext context, ForwardedResponse forwarded)
    {
        context.Response.StatusCode = forwarded.StatusCode;

        foreach (var header in forwarded.Headers)
            context.Response.Headers[header.Key] = header.Value;

        context.Response.Headers.Remove("Transfer-Encoding");

        await context.Response.WriteAsync(forwarded.Body);
    }

    /// <summary>
    /// Convenience overload: forwards to the backend and writes the response
    /// to the client in a single call (used for non-buffered paths).
    /// </summary>
    async Task ForwardRequestAsync(HttpContext context, EmulatorInstance instance, string path)
    {
        var forwarded = await ForwardToBackendAsync(context, instance, path);
        await WriteForwardedResponseAsync(context, forwarded);
    }

    // ── Emulator timespan clamping ─────────────────────────────────────────

    // The Azure ServiceBus Emulator enforces a strict 1-hour ceiling on
    // duration properties.  Real Azure ServiceBus accepts up to 14+ days.
    // MassTransit defaults to 366 days.  Rather than force every consumer
    // to override settings, the proxy transparently clamps durations in
    // the Atom XML body before forwarding to the emulator.

    static readonly TimeSpan EmulatorMaxTtl = TimeSpan.FromHours(1);

    static readonly HashSet<string> TimespanElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "DefaultMessageTimeToLive",
        "DuplicateDetectionHistoryTimeWindow",
        "LockDuration",
        "AutoDeleteOnIdle"
    };

    string ClampTimespanProperties(string xmlBody)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xmlBody); }
        catch { return xmlBody; } // not valid XML — pass through unchanged

        var changed = false;

        foreach (var element in doc.Descendants())
        {
            if (element.Name.Namespace != SbNs) continue;
            if (!TimespanElements.Contains(element.Name.LocalName)) continue;

            TimeSpan parsed;
            try { parsed = XmlConvert.ToTimeSpan(element.Value); }
            catch { continue; }

            if (parsed <= EmulatorMaxTtl) continue;

            var original = element.Value;
            element.Value = XmlConvert.ToString(EmulatorMaxTtl);
            changed = true;
            _logger.LogDebug("Clamped {Element} from {Original} to {Clamped}",
                element.Name.LocalName, original, element.Value);
        }

        return changed ? doc.ToString(SaveOptions.DisableFormatting) : xmlBody;
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

    /// <summary>
    /// Parse topic and subscription names from a subscription management path.
    /// /my-topic/Subscriptions/sub-1 → (my-topic, sub-1)
    /// </summary>
    static (string topic, string subscription)? ParseSubscriptionFromPath(string path)
    {
        var trimmed = path.TrimStart('/');
        var parts = trimmed.Split('/');
        if (parts.Length >= 3 && parts[1].Equals("Subscriptions", StringComparison.OrdinalIgnoreCase))
            return (parts[0], parts[2]);
        return null;
    }
}
