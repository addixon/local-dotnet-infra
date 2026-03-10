using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

// ─── Program ─────────────────────────────────────────────────────────────────

internal static class Program
{
    // ── State ────────────────────────────────────────────────────────────────

    static readonly ConcurrentQueue<MonitoredMessage> _incoming = new();
    static readonly List<MonitoredMessage> _messages = new();
    const int MaxMessageCount = 1000; // Cap to prevent memory exhaustion
    static int _selectedIndex;
    static int _scrollOffset;
    static bool _showingDetail;
    static int _detailScrollOffset;
    static bool _needsRedraw = true;
    static readonly CancellationTokenSource _cts = new();

    // Dynamic subscription tracking
    const int ConfigPollIntervalMs = 5000;
    static readonly ConcurrentDictionary<(string Topic, string Subscription), bool> _activeEndpoints = new();
    static readonly ConcurrentDictionary<string, bool> _activeTopics = new();

    // ── ANSI helpers (mirrors stack.ps1 style) ───────────────────────────────

    static string C(string code, string text) => $"\x1b[{code}m{text}\x1b[0m";
    static string Dim(string t)     => C("2",    t);
    static string Bold(string t)    => C("1",    t);
    static string Cyan(string t)    => C("36",   t);
    static string BCyan(string t)   => C("1;36", t);
    static string BWhite(string t)  => C("1;97", t);
    static string BYellow(string t) => C("1;33", t);

    // ── Entry point ──────────────────────────────────────────────────────────

    static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: ServiceBusMonitor <config-path>");
            return 1;
        }

        var configPath = args[0];
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Config file not found: {configPath}");
            return 1;
        }

        // Well-known connection string for the Azure Service Bus Emulator.
        // "SAS_KEY_VALUE" is the default placeholder accepted by the emulator;
        // it does not represent a real secret.
        const string connectionString =
            "Endpoint=sb://localhost;" +
            "SharedAccessKeyName=RootManageSharedAccessKey;" +
            "SharedAccessKey=SAS_KEY_VALUE;" +
            "UseDevelopmentEmulator=true;";

        await using var client = new ServiceBusClient(connectionString);

        // Load initial subscriptions from config (may be empty)
        LoadEndpointsFromConfig(configPath, client, _cts.Token);

        // Setup console
        Console.CursorVisible = false;
        Console.Clear();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cts.Cancel();
        };

        // Watch config for new subscriptions
        _ = WatchConfigAsync(configPath, client, _cts.Token);

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

    // ── Config loading & watching ────────────────────────────────────────────

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
            Console.Error.WriteLine($"[monitor] Failed to read config {configPath}: {ex.Message}");
            return;
        }

        var namespaces = config?.UserConfig?.Namespaces ?? [];

        foreach (var ns in namespaces)
        {
            foreach (var topic in ns.Topics ?? [])
            {
                _activeTopics.TryAdd(topic.Name, true);

                foreach (var sub in topic.Subscriptions ?? [])
                {
                    var key = (topic.Name, sub.Name);
                    if (_activeEndpoints.TryAdd(key, true))
                    {
                        _needsRedraw = true;
                        _ = PollMessagesAsync(client, topic.Name, sub.Name, ct);
                    }
                }
            }
        }
    }

    static async Task WatchConfigAsync(string configPath, ServiceBusClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(ConfigPollIntervalMs, ct); }
            catch (OperationCanceledException) { break; }

            LoadEndpointsFromConfig(configPath, client, ct);
        }
    }

    // ── Message polling ──────────────────────────────────────────────────────

    static async Task PollMessagesAsync(
        ServiceBusClient client,
        string topicName,
        string subscriptionName,
        CancellationToken ct)
    {
        var receiver = client.CreateReceiver(topicName, subscriptionName,
            new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });

        long lastSeq = 0;
        int retryDelay = 1000;
        const int maxRetryDelay = 30000;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var batch = await receiver.PeekMessagesAsync(
                        maxMessages: 50,
                        fromSequenceNumber: lastSeq + 1,
                        cancellationToken: ct);

                    // Reset retry delay on success
                    retryDelay = 1000;

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
                }
                catch (OperationCanceledException) { break; }
                catch (ServiceBusException ex)
                {
                    // Log Service Bus errors (e.g. connection lost, unauthorized) and back off
                    Console.Error.WriteLine($"[monitor] Service Bus error on {topicName}/{subscriptionName}: {ex.Message} (retrying in {retryDelay / 1000}s)");

                    try { await Task.Delay(retryDelay, ct); }
                    catch (OperationCanceledException) { break; }

                    // Exponential backoff with cap
                    retryDelay = Math.Min(retryDelay * 2, maxRetryDelay);
                    continue;
                }
                catch (Exception ex)
                {
                    // Unexpected error — surface once via stderr, then continue polling
                    Console.Error.WriteLine($"[monitor] Error polling {topicName}/{subscriptionName}: {ex.Message}");
                }

                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            await receiver.DisposeAsync();
        }
    }

    // ── UI loop ──────────────────────────────────────────────────────────────

    static async Task RunUiLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            // Drain incoming queue
            while (_incoming.TryDequeue(out var msg))
            {
                _messages.Insert(0, msg); // newest first

                // Cap list size to prevent memory exhaustion
                if (_messages.Count > MaxMessageCount)
                    _messages.RemoveAt(_messages.Count - 1);

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
                if (_showingDetail)
                    RenderDetail();
                else
                {
                    var topicCount = _activeTopics.Count;
                    var subscriptionCount = _activeEndpoints.Count;
                    var topicNames = string.Join(", ", _activeTopics.Keys.OrderBy(t => t));
                    RenderList(topicCount, subscriptionCount, topicNames);
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

        if (_showingDetail)
        {
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    _showingDetail = false;
                    _detailScrollOffset = 0;
                    break;
                case ConsoleKey.UpArrow:
                    _detailScrollOffset = Math.Max(0, _detailScrollOffset - 1);
                    break;
                case ConsoleKey.DownArrow:
                    _detailScrollOffset++;
                    break;
            }
        }
        else
        {
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
                        _showingDetail = true;
                        _detailScrollOffset = 0;
                    }
                    break;
            }
        }
    }

    // ── List view ────────────────────────────────────────────────────────────

    static void RenderList(int topicCount, int subscriptionCount, string topicNames)
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
        if (subscriptionCount == 0)
        {
            sb.AppendLine($"  {BCyan("▶")} Waiting for subscriptions to appear in config…");
            sb.AppendLine($"    {Dim($"The config file is checked every {ConfigPollIntervalMs / 1000} seconds.")}");
        }
        else
        {
            sb.AppendLine($"  {BCyan("▶")} Monitoring {Bold(topicCount.ToString())} topic(s) across {Bold(subscriptionCount.ToString())} subscription(s)");
            sb.AppendLine($"    {Dim($"Topics: {topicNames}")}");
        }
        sb.AppendLine();
        sb.AppendLine($"    {Dim("Use ↑↓ to navigate · Enter to view · q or Esc to quit")}");
        sb.AppendLine();

        // Column headers
        const int colTime  = 22;
        const int colTopic = 20;
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
        sb.AppendLine($"  {Dim($"{_messages.Count} message(s) received")}");

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
