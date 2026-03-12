using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Azure.Messaging.ServiceBus;

// ─── JSON configuration models ───────────────────────────────────────────────

sealed record SbEmulatorConfig(
    [property: JsonPropertyName("UserConfig")] SbUserConfig UserConfig);

sealed record SbUserConfig(
    [property: JsonPropertyName("Namespaces")] List<SbNamespace> Namespaces);

sealed record SbNamespace(
    [property: JsonPropertyName("Name")]   string Name,
    [property: JsonPropertyName("Topics")] List<SbTopic>? Topics);

sealed record SbTopic(
    [property: JsonPropertyName("Name")]          string Name,
    [property: JsonPropertyName("Subscriptions")] List<SbSubscription>? Subscriptions);

sealed record SbSubscription(
    [property: JsonPropertyName("Name")] string Name);

// ─── Monitored message ──────────────────────────────────────────────────────

sealed record MonitoredMessage(
    string MessageId,
    string TopicName,
    string SubscriptionName,
    DateTimeOffset EnqueuedTime,
    string? Subject,
    IReadOnlyDictionary<string, object> ApplicationProperties,
    string Body,
    long SequenceNumber);

sealed record MonitoredError(
    DateTimeOffset Timestamp,
    string TopicName,
    string SubscriptionName,
    string Message);

// ─── Program ─────────────────────────────────────────────────────────────────

internal static class Program
{
    // ── State ────────────────────────────────────────────────────────────────

    static readonly ConcurrentQueue<MonitoredMessage> _incoming = new();
    static readonly List<MonitoredMessage> _messages = new();
    const int MaxMessageCount = 1000; // Cap to prevent memory exhaustion
    static int _selectedIndex;
    static int _scrollOffset;
    static bool _needsRedraw = true;
    static readonly CancellationTokenSource _cts = new();

    // Error tracking — errors go to an in-memory list instead of stderr
    static readonly ConcurrentQueue<MonitoredError> _incomingErrors = new();
    static readonly List<MonitoredError> _errors = new();
    const int MaxErrorCount = 500;
    static int _errorSelectedIndex;
    static int _errorScrollOffset;

    // View state
    enum ViewMode { Messages, Detail, Errors }
    static ViewMode _view = ViewMode.Messages;
    static int _detailScrollOffset;

    // Dynamic discovery — tracks topic/subscription pairs via the management API
    const int DiscoveryPollIntervalMs = 5000;
    static readonly ConcurrentDictionary<string, bool> _activeSubscriptions = new();
    static string _managementUrl = "http://localhost:5300";
    static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    static readonly XNamespace SbNs = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";

    // ── ANSI helpers (mirrors stack.ps1 style) ───────────────────────────────

    static string C(string code, string text) => $"\x1b[{code}m{text}\x1b[0m";
    static string Dim(string t)     => C("2",    t);
    static string Bold(string t)    => C("1",    t);
    static string Cyan(string t)    => C("36",   t);
    static string BCyan(string t)   => C("1;36", t);
    static string BWhite(string t)  => C("1;97", t);
    static string BYellow(string t) => C("1;33", t);
    static string BRed(string t)    => C("1;31", t);
    static string Red(string t)     => C("31",   t);

    // ── Entry point ──────────────────────────────────────────────────────────

    static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: ServiceBusMonitor <management-url> [config-path]");
            Console.Error.WriteLine("  e.g.: ServiceBusMonitor http://localhost:80");
            Console.Error.WriteLine("  e.g.: ServiceBusMonitor http://localhost:80 servicebus/Config.json");
            return 1;
        }

        _managementUrl = args[0].TrimEnd('/');

        // Optional config path for initial topic seeding (backwards compatibility)
        string? configPath = args.Length >= 2 ? args[1] : null;

        // Well-known connection string for the Azure Service Bus Emulator.
        // "SAS_KEY_VALUE" is the default placeholder accepted by the emulator;
        // it does not represent a real secret.
        const string connectionString =
            "Endpoint=sb://localhost;" +
            "SharedAccessKeyName=RootManageSharedAccessKey;" +
            "SharedAccessKey=SAS_KEY_VALUE;" +
            "UseDevelopmentEmulator=true;";

        await using var client = new ServiceBusClient(connectionString);

        // Seed from config if provided
        if (configPath is not null && File.Exists(configPath))
            LoadEndpointsFromConfig(configPath, client, _cts.Token);

        // Setup console
        Console.CursorVisible = false;
        Console.Clear();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cts.Cancel();
        };

        // Discover topics/subscriptions from the management API
        _ = DiscoverFromManagementApiAsync(client, _cts.Token);

        try
        {
            await RunUiLoopAsync();
        }
        catch (OperationCanceledException) { /* normal exit */ }
        finally
        {
            Console.CursorVisible = true;
            Console.Clear();
        }

        return 0;
    }

    // ── Config loading (optional seed) ─────────────────────────────────────

    static void LoadEndpointsFromConfig(string configPath, ServiceBusClient client, CancellationToken ct)
    {
        SbEmulatorConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<SbEmulatorConfig>(
                File.ReadAllText(configPath));
        }
        catch (Exception ex)
        {
            _incomingErrors.Enqueue(new MonitoredError(
                DateTimeOffset.Now, "(config)", configPath, $"Failed to read config: {ex.Message}"));
            _needsRedraw = true;
            return;
        }

        var namespaces = config?.UserConfig?.Namespaces ?? [];

        foreach (var ns in namespaces)
        {
            foreach (var topic in ns.Topics ?? [])
            {
                foreach (var sub in topic.Subscriptions ?? [])
                {
                    StartMonitoring(client, topic.Name, sub.Name, ct);
                }
            }
        }
    }

    // ── Management API discovery ─────────────────────────────────────────────

    static async Task DiscoverFromManagementApiAsync(ServiceBusClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var topics = await GetTopicNamesAsync(ct);
                foreach (var topic in topics)
                {
                    var subs = await GetSubscriptionNamesAsync(topic, ct);
                    foreach (var sub in subs)
                    {
                        StartMonitoring(client, topic, sub, ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _incomingErrors.Enqueue(new MonitoredError(
                    DateTimeOffset.Now, "(discovery)", _managementUrl, ex.Message));
                _needsRedraw = true;
            }

            try { await Task.Delay(DiscoveryPollIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    static async Task<List<string>> GetTopicNamesAsync(CancellationToken ct)
    {
        var names = new List<string>();
        var resp  = await _httpClient.GetStringAsync($"{_managementUrl}/$Resources/Topics", ct);
        try
        {
            var doc = XDocument.Parse(resp);
            foreach (var entry in doc.Descendants(Atom + "entry"))
            {
                var title = entry.Element(Atom + "title")?.Value;
                if (!string.IsNullOrWhiteSpace(title))
                    names.Add(title);
            }
        }
        catch { /* non-XML response */ }
        return names;
    }

    static async Task<List<string>> GetSubscriptionNamesAsync(string topicName, CancellationToken ct)
    {
        var names = new List<string>();
        try
        {
            var resp = await _httpClient.GetStringAsync(
                $"{_managementUrl}/{Uri.EscapeDataString(topicName)}/Subscriptions", ct);
            var doc = XDocument.Parse(resp);
            foreach (var entry in doc.Descendants(Atom + "entry"))
            {
                var title = entry.Element(Atom + "title")?.Value;
                if (!string.IsNullOrWhiteSpace(title))
                    names.Add(title);
            }
        }
        catch { /* topic may not have subscriptions yet */ }
        return names;
    }

    static void StartMonitoring(ServiceBusClient client, string topicName, string subName, CancellationToken ct)
    {
        var key = $"{topicName}/{subName}";
        if (_activeSubscriptions.TryAdd(key, true))
        {
            _needsRedraw = true;
            _ = PollMessagesAsync(client, topicName, subName, ct);
        }
    }

    // ── Message polling ──────────────────────────────────────────────────────

    static async Task PollMessagesAsync(
        ServiceBusClient client,
        string topicName,
        string subscriptionName,
        CancellationToken ct)
    {
        long lastSeq = 0;
        int retryDelay = 1000;
        const int maxRetryDelay = 30000;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Create a fresh receiver each outer iteration so stale
                // connections ("owner is being closed") are recovered.
                await using var receiver = client.CreateReceiver(topicName, subscriptionName,
                    new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var batch = await receiver.PeekMessagesAsync(
                            maxMessages: 50,
                            fromSequenceNumber: lastSeq + 1,
                            cancellationToken: ct);

                        retryDelay = 1000; // reset on success

                        foreach (var msg in batch)
                        {
                            if (msg.SequenceNumber <= lastSeq) continue;
                            lastSeq = msg.SequenceNumber;

                            string body;
                            try { body = msg.Body.ToString(); }
                            catch { body = "(unable to read body)"; }

                            var props = new Dictionary<string, object>();
                            foreach (var kvp in msg.ApplicationProperties)
                                props[kvp.Key] = kvp.Value;

                            _incoming.Enqueue(new MonitoredMessage(
                                msg.MessageId,
                                topicName,
                                subscriptionName,
                                msg.EnqueuedTime,
                                msg.Subject,
                                props,
                                body,
                                msg.SequenceNumber));
                        }

                        try { await Task.Delay(1000, ct); }
                        catch (OperationCanceledException) { return; }
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (ServiceBusException ex)
                {
                    _incomingErrors.Enqueue(new MonitoredError(
                        DateTimeOffset.Now, topicName, subscriptionName, ex.Message));
                    _needsRedraw = true;
                }
                catch (Exception ex)
                {
                    _incomingErrors.Enqueue(new MonitoredError(
                        DateTimeOffset.Now, topicName, subscriptionName, ex.Message));
                    _needsRedraw = true;
                }

                // Back off before creating a new receiver
                try { await Task.Delay(retryDelay, ct); }
                catch (OperationCanceledException) { return; }
                retryDelay = Math.Min(retryDelay * 2, maxRetryDelay);
            }
        }
        catch (OperationCanceledException) { /* normal exit */ }
    }

    // ── UI loop ──────────────────────────────────────────────────────────────

    static async Task RunUiLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            // Drain incoming messages
            while (_incoming.TryDequeue(out var msg))
            {
                _messages.Insert(0, msg); // newest first
                if (_messages.Count > MaxMessageCount)
                    _messages.RemoveAt(_messages.Count - 1);
                _needsRedraw = true;
            }

            // Drain incoming errors
            while (_incomingErrors.TryDequeue(out var err))
            {
                _errors.Insert(0, err); // newest first
                if (_errors.Count > MaxErrorCount)
                    _errors.RemoveAt(_errors.Count - 1);
                _needsRedraw = true;
            }

            // Handle keyboard
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                HandleKey(key);
            }

            // Render
            if (_needsRedraw)
            {
                switch (_view)
                {
                    case ViewMode.Detail:
                        RenderDetail();
                        break;
                    case ViewMode.Errors:
                        RenderErrors();
                        break;
                    default:
                    {
                        var subCount = _activeSubscriptions.Count;
                        var subNames = string.Join(", ", _activeSubscriptions.Keys.OrderBy(t => t));
                        RenderList(subCount, subNames);
                        break;
                    }
                }
                _needsRedraw = false;
            }

            await Task.Delay(150, _cts.Token);
        }
    }

    // ── Key handling ─────────────────────────────────────────────────────────

    static void HandleKey(ConsoleKeyInfo key)
    {
        _needsRedraw = true;

        // Exit on 'q' or 'Q' from any view
        if (key.KeyChar is 'q' or 'Q')
        {
            _cts.Cancel();
            return;
        }

        switch (_view)
        {
            case ViewMode.Detail:
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        _view = ViewMode.Messages;
                        _detailScrollOffset = 0;
                        break;
                    case ConsoleKey.UpArrow:
                        _detailScrollOffset = Math.Max(0, _detailScrollOffset - 1);
                        break;
                    case ConsoleKey.DownArrow:
                        _detailScrollOffset++;
                        break;
                }
                break;

            case ViewMode.Errors:
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        _view = ViewMode.Messages;
                        break;
                    case ConsoleKey.UpArrow:
                        _errorSelectedIndex = Math.Max(0, _errorSelectedIndex - 1);
                        break;
                    case ConsoleKey.DownArrow:
                        if (_errors.Count > 0)
                            _errorSelectedIndex = Math.Min(_errors.Count - 1, _errorSelectedIndex + 1);
                        break;
                }
                break;

            default: // Messages
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        _cts.Cancel();
                        return;
                    case ConsoleKey.UpArrow:
                        _selectedIndex = Math.Max(0, _selectedIndex - 1);
                        break;
                    case ConsoleKey.DownArrow:
                        if (_messages.Count > 0)
                            _selectedIndex = Math.Min(_messages.Count - 1, _selectedIndex + 1);
                        break;
                    case ConsoleKey.Enter:
                        if (_messages.Count > 0 && _selectedIndex < _messages.Count)
                        {
                            _view = ViewMode.Detail;
                            _detailScrollOffset = 0;
                        }
                        break;
                }
                // 'e' / 'E' to toggle errors view
                if (key.KeyChar is 'e' or 'E')
                    _view = ViewMode.Errors;
                break;
        }
    }

    // ── List view ────────────────────────────────────────────────────────────

    static void RenderList(int subCount, string subNames)
    {
        var sb = new StringBuilder();
        var width  = Math.Max(SafeWindowWidth(), 80);
        var height = Math.Max(SafeWindowHeight(), 24);

        sb.Append("\x1b[H\x1b[J"); // home + clear

        // Banner
        sb.AppendLine();
        var bar = new string('─', 53);
        sb.AppendLine($"  {BCyan($"╭{bar}╮")}");
        sb.AppendLine($"  {BCyan("│")}    {BWhite("local-infrastructure")}  {Dim("·  Service Bus Monitor")}    {BCyan("│")}");
        sb.AppendLine($"  {BCyan($"╰{bar}╯")}");
        sb.AppendLine();

        // Info
        if (subCount == 0)
        {
            sb.AppendLine($"  {BCyan("▶")} Discovering topics and subscriptions…");
            sb.AppendLine($"    {Dim($"The management API is polled every {DiscoveryPollIntervalMs / 1000} seconds.")}");
        }
        else
        {
            sb.AppendLine($"  {BCyan("▶")} Monitoring {Bold(subCount.ToString())} subscription(s)");
            sb.AppendLine($"    {Dim($"Endpoints: {subNames}")}");
        }
        sb.AppendLine();
        sb.AppendLine($"    {Dim("Use ↑↓ to navigate · Enter to view · e for errors · q or Esc to quit")}");
        sb.AppendLine();

        // Column headers
        const int colTime  = 22;
        const int colTopic = 40;
        const int colSub   = 20;
        sb.Append("  ");
        sb.Append(Dim("  RECEIVED".PadRight(colTime)));
        sb.Append(Dim("TOPIC".PadRight(colTopic)));
        sb.Append(Dim("SUBSCRIPTION".PadRight(colSub)));
        sb.AppendLine(Dim("SUBJECT / ID"));
        sb.AppendLine($"  {Dim(new string('─', Math.Min(width - 4, 100)))}");

        const int headerLines = 12;
        var availableLines = height - headerLines - 3;

        if (_messages.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine($"    {Dim("No messages received yet. Waiting…")}");
        }
        else
        {
            // Keep selection in bounds
            _selectedIndex = Math.Clamp(_selectedIndex, 0, _messages.Count - 1);

            // Keep selected item visible
            if (_selectedIndex < _scrollOffset)
                _scrollOffset = _selectedIndex;
            if (_selectedIndex >= _scrollOffset + availableLines)
                _scrollOffset = _selectedIndex - availableLines + 1;

            var visible = _messages.Skip(_scrollOffset).Take(availableLines).ToList();
            for (var i = 0; i < visible.Count; i++)
            {
                var msg = visible[i];
                var idx = _scrollOffset + i;
                var selected = idx == _selectedIndex;

                var time    = msg.EnqueuedTime.LocalDateTime.ToString("HH:mm:ss.fff");
                var topic   = Truncate(msg.TopicName, colTopic - 2);
                var sub     = Truncate(msg.SubscriptionName, colSub - 2);
                var subject = msg.Subject ?? msg.MessageId ?? "(no id)";

                var ptr  = selected ? BCyan("▸") : " ";
                var line = $"  {ptr} {time.PadRight(colTime - 2)}{topic.PadRight(colTopic)}{sub.PadRight(colSub)}{subject}";

                sb.AppendLine(selected ? Bold(line) : line);
            }
        }

        // Footer
        sb.AppendLine();
        var errorHint = _errors.Count > 0
            ? $"  ·  {BRed($"{_errors.Count} error(s)")} {Dim("(press e)")}"
            : "";
        sb.AppendLine($"  {Dim($"{_messages.Count} message(s) received")}{errorHint}");

        Console.Write(sb.ToString());
    }

    // ── Detail / modal view ──────────────────────────────────────────────────

    static void RenderDetail()
    {
        if (_selectedIndex >= _messages.Count) return;

        var msg    = _messages[_selectedIndex];
        var sb     = new StringBuilder();
        var width  = Math.Max(SafeWindowWidth(), 80);
        var height = Math.Max(SafeWindowHeight(), 24);

        sb.Append("\x1b[H\x1b[J"); // home + clear

        // Modal header
        sb.AppendLine();
        var innerW    = Math.Min(width - 6, 70);
        var bar       = new string('─', innerW);
        var title     = "Message Details";
        var hint      = "Press Esc to close";
        var pad       = Math.Max(1, innerW - title.Length - hint.Length - 2);

        sb.AppendLine($"  {BCyan($"╭{bar}╮")}");
        sb.AppendLine($"  {BCyan("│")} {BWhite(title)}{new string(' ', pad)}{Dim(hint)} {BCyan("│")}");
        sb.AppendLine($"  {BCyan($"╰{bar}╯")}");
        sb.AppendLine();

        // Build content lines
        var lines = new List<string>
        {
            $"    {Dim("Message ID:")}    {msg.MessageId}",
            $"    {Dim("Subject:")}       {msg.Subject ?? "(none)"}",
            $"    {Dim("Topic:")}         {msg.TopicName}",
            $"    {Dim("Subscription:")}  {msg.SubscriptionName}",
            $"    {Dim("Enqueued:")}      {msg.EnqueuedTime.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff}",
            $"    {Dim("Sequence #:")}    {msg.SequenceNumber}",
            ""
        };

        // Application properties
        var propBar = new string('─', Math.Max(0, innerW - 28));
        lines.Add($"    {Cyan($"── Application Properties {propBar}")}");
        if (msg.ApplicationProperties.Count == 0)
        {
            lines.Add($"    {Dim("(none)")}");
        }
        else
        {
            foreach (var kvp in msg.ApplicationProperties)
                lines.Add($"    {Dim(kvp.Key + ":")}  {kvp.Value}");
        }
        lines.Add("");

        // Body
        var bodyBar = new string('─', Math.Max(0, innerW - 8));
        lines.Add($"    {Cyan($"── Body {bodyBar}")}");

        var bodyText = msg.Body;
        try
        {
            var jsonDoc = JsonDocument.Parse(bodyText);
            bodyText = JsonSerializer.Serialize(jsonDoc,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch { /* not JSON — show raw */ }

        foreach (var bodyLine in bodyText.Split('\n'))
            lines.Add($"    {bodyLine.TrimEnd('\r')}");

        // Clamp scroll
        const int modalHeaderLines = 5;
        var availableLines = height - modalHeaderLines - 3;
        _detailScrollOffset = Math.Clamp(_detailScrollOffset, 0,
            Math.Max(0, lines.Count - availableLines));

        var visible = lines.Skip(_detailScrollOffset).Take(availableLines);
        foreach (var line in visible)
            sb.AppendLine(line);

        // Scroll indicator
        sb.AppendLine();
        if (lines.Count > availableLines)
        {
            var from = _detailScrollOffset + 1;
            var to   = Math.Min(_detailScrollOffset + availableLines, lines.Count);
            sb.AppendLine($"    {Dim($"↑↓ to scroll · Lines {from}–{to} of {lines.Count}")}");
        }

        Console.Write(sb.ToString());
    }

    // ── Errors view ─────────────────────────────────────────────────────────

    static void RenderErrors()
    {
        var sb = new StringBuilder();
        var width  = Math.Max(SafeWindowWidth(), 80);
        var height = Math.Max(SafeWindowHeight(), 24);

        sb.Append("\x1b[H\x1b[J"); // home + clear

        // Banner
        sb.AppendLine();
        var bar = new string('─', 53);
        sb.AppendLine($"  {BRed($"╭{bar}╮")}");
        sb.AppendLine($"  {BRed("│")}    {BWhite("local-infrastructure")}  {Dim("·  Errors")}                 {BRed("│")}");
        sb.AppendLine($"  {BRed($"╰{bar}╯")}");
        sb.AppendLine();
        sb.AppendLine($"    {Dim("Use ↑↓ to navigate · Esc to return · q to quit")}");
        sb.AppendLine();

        // Column headers
        const int colTime  = 22;
        const int colTopic = 40;
        sb.Append("  ");
        sb.Append(Dim("  TIME".PadRight(colTime)));
        sb.Append(Dim("TOPIC / SUBSCRIPTION".PadRight(colTopic)));
        sb.AppendLine(Dim("ERROR"));
        sb.AppendLine($"  {Dim(new string('─', Math.Min(width - 4, 120)))}");

        const int headerLines = 10;
        var availableLines = height - headerLines - 3;

        if (_errors.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine($"    {Dim("No errors recorded.")}");
        }
        else
        {
            _errorSelectedIndex = Math.Clamp(_errorSelectedIndex, 0, _errors.Count - 1);

            if (_errorSelectedIndex < _errorScrollOffset)
                _errorScrollOffset = _errorSelectedIndex;
            if (_errorSelectedIndex >= _errorScrollOffset + availableLines)
                _errorScrollOffset = _errorSelectedIndex - availableLines + 1;

            var visible = _errors.Skip(_errorScrollOffset).Take(availableLines).ToList();
            for (var i = 0; i < visible.Count; i++)
            {
                var err = visible[i];
                var idx = _errorScrollOffset + i;
                var selected = idx == _errorSelectedIndex;

                var time    = err.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff");
                var entity  = Truncate($"{err.TopicName}/{err.SubscriptionName}", colTopic - 2);
                var errMsg  = Truncate(err.Message, Math.Max(20, width - colTime - colTopic - 8));

                var ptr  = selected ? BRed("▸") : " ";
                var line = $"  {ptr} {time.PadRight(colTime - 2)}{entity.PadRight(colTopic)}{Red(errMsg)}";

                sb.AppendLine(selected ? Bold(line) : line);
            }
        }

        // Footer
        sb.AppendLine();
        sb.AppendLine($"  {Dim($"{_errors.Count} error(s) recorded")}");

        Console.Write(sb.ToString());
    }

    // ── Utilities ────────────────────────────────────────────────────────────

    static string Truncate(string value, int maxLen)
    {
        if (value.Length <= maxLen) return value;
        return string.Concat(value.AsSpan(0, maxLen - 1), "…");
    }

    static int SafeWindowWidth()
    {
        try { return Console.WindowWidth; }
        catch { return 120; }
    }

    static int SafeWindowHeight()
    {
        try { return Console.WindowHeight; }
        catch { return 30; }
    }
}
