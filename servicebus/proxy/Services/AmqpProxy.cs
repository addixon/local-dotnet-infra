using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ServiceBusProxy.Models;

namespace ServiceBusProxy.Services;

// ─── AMQP TCP proxy ─────────────────────────────────────────────────────────
// A raw TCP proxy that handles the AMQP 1.0 protocol at the frame level.
// Supports the MSSBCBS SASL mechanism required by the Azure ServiceBus SDK.
//
// Flow per connection:
//   1. Handle SASL MSSBCBS with client (proxy acts as server)
//   2. Handle AMQP Open + Begin with client
//   3. Handle CBS authentication locally (accept any token)
//   4. Wait for entity Attach → determine backend emulator
//   5. Connect to backend, replay handshake (SASL + Open + Begin + CBS)
//   6. Forward entity Attach, then bidirectional byte relay
//
// Current model: one AMQP connection per entity (no multiplexing).
// See docs/FUTURE-MULTIPLEXING.md for the multiplexing roadmap.

sealed class AmqpProxy : IDisposable
{
    readonly EntityRouter         _router;
    readonly EmulatorOrchestrator _orchestrator;
    readonly ILogger              _logger;
    readonly int                  _listenPort;

    TcpListener?          _listener;
    CancellationTokenSource _cts = new();

    // AMQP protocol headers
    static readonly byte[] SaslHeader = "AMQP\x03\x01\x00\x00"u8.ToArray();
    static readonly byte[] AmqpHeader = "AMQP\x00\x01\x00\x00"u8.ToArray();

    public AmqpProxy(
        EntityRouter         router,
        EmulatorOrchestrator orchestrator,
        ILogger<AmqpProxy>   logger,
        IConfiguration       config)
    {
        _router       = router;
        _orchestrator = orchestrator;
        _logger       = logger;
        _listenPort   = int.Parse(config["PROXY_AMQP_PORT"] ?? "5672");
    }

    // ── Start / Stop ─────────────────────────────────────────────────────────

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _listenPort);
        _listener.Start();
        _ = AcceptLoopAsync();
        _logger.LogInformation("AMQP TCP proxy listening on port {Port}", _listenPort);
    }

    public void Stop()
    {
        _cts.Cancel();
        _listener?.Stop();
        _logger.LogInformation("AMQP proxy stopped");
    }

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleConnectionAsync(client));
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error accepting TCP connection");
            }
        }
    }

    // ── Per-connection handler ───────────────────────────────────────────────

    async Task HandleConnectionAsync(TcpClient client)
    {
        using var _ = client;
        var cs = client.GetStream();
        var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";

        try
        {
            _logger.LogDebug("New AMQP connection from {Ep}", ep);

            // ── Phase 1: SASL with client ────────────────────────────────────
            // The Azure ServiceBus SDK requires the MSSBCBS SASL mechanism.
            // We handle it ourselves (trivially accept) then do MSSBCBS with
            // the backend emulator too.

            await ReadExactAsync(cs, 8); // client sends SASL header
            await cs.WriteAsync(SaslHeader);
            await WriteFrameAsync(cs, 1, 0, BuildSaslMechanisms());

            var (_, initPayload) = await ReadFrameAsync(cs); // SaslInit
            _logger.LogDebug("Client {Ep} SASL: {Mech}", ep, ParseSaslInitMechanism(initPayload));

            await WriteFrameAsync(cs, 1, 0, BuildSaslOutcome());

            // ── Phase 2: Connect to first healthy backend ────────────────────
            // For single-shard configs, all entities are on one backend.
            // For multi-shard, this approach routes the entire connection to
            // one backend (entity routing is deferred to a future version).
            var instance = _router.AllInstances.FirstOrDefault(i => i.Healthy);
            if (instance is null)
            {
                _logger.LogWarning("No healthy backends — closing {Ep}", ep);
                return;
            }
            _logger.LogDebug("Connecting {Ep} → backend {Container}", ep, instance.ContainerName);

            using var backend = new TcpClient();
            await backend.ConnectAsync(instance.ContainerName, 5672);
            var bs = backend.GetStream();

            // Do SASL MSSBCBS with backend
            await bs.WriteAsync(SaslHeader);
            await ReadExactAsync(bs, 8); // backend SASL header
            await ReadFrameAsync(bs);     // SaslMechanisms from backend
            await WriteFrameAsync(bs, 1, 0, BuildSaslInit());
            await ReadFrameAsync(bs);     // SaslOutcome from backend

            // ── Phase 3: Relay AMQP protocol header ──────────────────────────
            // Client sends AMQP header → forward to backend
            // Backend responds → forward to client
            var clientAmqpHdr = await ReadExactAsync(cs, 8);
            await bs.WriteAsync(clientAmqpHdr);
            var backendAmqpHdr = await ReadExactAsync(bs, 8);
            await cs.WriteAsync(backendAmqpHdr);

            _logger.LogDebug("Connection {Ep} relayed to {Container} — starting byte relay",
                ep, instance.ContainerName);

            // ── Phase 4: Bidirectional byte relay ────────────────────────────
            // All remaining AMQP frames (Open, Begin, CBS, Attach, Transfer,
            // etc.) are transparently relayed between client and backend.
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var c2b = RelayAsync(cs, bs, cts);
            var b2c = RelayAsync(bs, cs, cts);
            await Task.WhenAny(c2b, b2c);
            cts.Cancel();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("Connection {Ep} closed: {Msg}", ep, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling connection from {Ep}", ep);
        }
    }

    // ── Bidirectional relay ──────────────────────────────────────────────────

    static async Task RelayAsync(NetworkStream src, NetworkStream dst, CancellationTokenSource cts)
    {
        var buf = new byte[8192];
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var n = await src.ReadAsync(buf, cts.Token);
                if (n == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), cts.Token);
            }
        }
        catch { /* connection closed */ }
        finally { cts.Cancel(); }
    }

    // ── Frame I/O ────────────────────────────────────────────────────────────

    static async Task<byte[]> ReadExactAsync(NetworkStream s, int count)
    {
        var buf = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(offset, count - offset));
            if (n == 0) throw new IOException("Connection closed");
            offset += n;
        }
        return buf;
    }

    /// <summary>Read one AMQP frame. Returns (8-byte header, payload bytes).</summary>
    static async Task<(byte[] header, byte[] payload)> ReadFrameAsync(NetworkStream s)
    {
        var hdr = await ReadExactAsync(s, 8);
        var size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
        if (size < 8) throw new InvalidDataException($"Invalid frame size: {size}");
        var payloadLen = (int)size - 8;
        var payload = payloadLen > 0 ? await ReadExactAsync(s, payloadLen) : [];
        return (hdr, payload);
    }

    /// <summary>Write an AMQP frame with the given type, channel, and payload.</summary>
    static async Task WriteFrameAsync(NetworkStream s, byte type, ushort channel, byte[] payload)
    {
        var size = (uint)(8 + payload.Length);
        var hdr = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(hdr, size);
        hdr[4] = 2; // doff
        hdr[5] = type;
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(6), channel);
        await s.WriteAsync(hdr);
        if (payload.Length > 0)
            await s.WriteAsync(payload);
    }

    static byte[] CombineFrame(byte[] header, byte[] payload)
    {
        var result = new byte[header.Length + payload.Length];
        header.CopyTo(result, 0);
        payload.CopyTo(result, header.Length);
        return result;
    }

    // ── AMQP type encoding helpers ───────────────────────────────────────────
    // Minimal AMQP 1.0 type encoding — just enough for the SASL handshake.

    /// <summary>Build SaslMechanisms frame payload offering MSSBCBS.</summary>
    static byte[] BuildSaslMechanisms()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x00); ms.WriteByte(0x53); ms.WriteByte(0x40); // described(0x40 = sasl-mechanisms)
        ms.WriteByte(0xC0); // list8
        var mechBytes = "MSSBCBS"u8;
        var listContentLen = 1 + 1 + mechBytes.Length; // sym8 format + len + bytes
        ms.WriteByte((byte)(listContentLen + 1)); // size (includes count byte)
        ms.WriteByte(0x01); // count = 1 field
        ms.WriteByte(0xA3); // sym8
        ms.WriteByte((byte)mechBytes.Length);
        ms.Write(mechBytes);
        return ms.ToArray();
    }

    /// <summary>Build SaslOutcome(code=ok) frame payload.</summary>
    static byte[] BuildSaslOutcome() =>
        [0x00, 0x53, 0x44, 0xC0, 0x03, 0x01, 0x50, 0x00];

    /// <summary>Build SaslInit(MSSBCBS) frame payload for backend connection.</summary>
    static byte[] BuildSaslInit()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x00); ms.WriteByte(0x53); ms.WriteByte(0x41); // described(0x41 = sasl-init)
        ms.WriteByte(0xC0); // list8
        var mechBytes = "MSSBCBS"u8;
        var contentLen = 1 + 1 + mechBytes.Length;
        ms.WriteByte((byte)(contentLen + 1)); // size
        ms.WriteByte(0x01); // count = 1
        ms.WriteByte(0xA3); // sym8
        ms.WriteByte((byte)mechBytes.Length);
        ms.Write(mechBytes);
        return ms.ToArray();
    }

    // ── AMQP parsing helpers ─────────────────────────────────────────────────

    /// <summary>Extract the mechanism name from a SaslInit frame payload.</summary>
    static string ParseSaslInitMechanism(byte[] payload)
    {
        var pos = SkipDescriptor(payload, 0);
        if (pos >= payload.Length) return "?";
        if (payload[pos] == 0xC0) pos += 3;      // list8: format + size + count
        else if (payload[pos] == 0xD0) pos += 9;  // list32: format + size(4) + count(4)
        else return "?";
        return ReadSymbol(payload, ref pos);
    }

    /// <summary>Skip a described type descriptor at the given position.</summary>
    static int SkipDescriptor(byte[] buf, int pos)
    {
        if (pos >= buf.Length || buf[pos] != 0x00) return pos;
        pos++;
        if (pos >= buf.Length) return pos;
        if (buf[pos] == 0x53) return pos + 2; // smallulong
        if (buf[pos] == 0x80) return pos + 9; // ulong
        return pos;
    }

    static string ReadSymbol(byte[] buf, ref int pos)
    {
        if (pos >= buf.Length) return "";
        var code = buf[pos++];
        return code switch
        {
            0x40 => "",
            0xA3 => ReadStr8(buf, ref pos),
            0xB3 => ReadStr32(buf, ref pos),
            _ => ""
        };
    }

    static string ReadStr8(byte[] buf, ref int pos)
    {
        if (pos >= buf.Length) return "";
        var len = buf[pos++];
        var s = Encoding.UTF8.GetString(buf, pos, len);
        pos += len;
        return s;
    }

    static string ReadStr32(byte[] buf, ref int pos)
    {
        if (pos + 4 > buf.Length) return "";
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos));
        pos += 4;
        var s = Encoding.UTF8.GetString(buf, pos, len);
        pos += len;
        return s;
    }

    public void Dispose()
    {
        Stop();
    }
}
