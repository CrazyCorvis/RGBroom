using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MagicHomeNET;
public sealed class MagicProtocol
{
    public ProtocolKind Kind { get; private set; }
    public bool WriteAll { get; private set; }
    byte sequence;
    public MagicProtocol(ProtocolKind kind = ProtocolKind.Rgb8, bool writeAll = true) { Kind = kind; WriteAll = writeAll; }
    public static byte[] Checksum(params byte[] raw) => raw.Concat(new[] { unchecked((byte)raw.Sum(x => x)) }).ToArray();
    public byte[] Wrap(byte[] inner, byte version = 1)
    {
        sequence++;
        return Checksum(new byte[] { 0xb0, 0xb1, 0xb2, 0xb3, 0, 1, version, sequence, (byte)(inner.Length >> 8), (byte)inner.Length }.Concat(inner).ToArray());
    }
    public byte[] Query() => Kind is ProtocolKind.Legacy or ProtocolKind.LegacyRgbw ? new byte[] { 0xef, 1, 0x77 } : Kind is ProtocolKind.Spi3 or ProtocolKind.Hsv25 ? Wrap(Checksum(0x81, 0x8a, 0x8b)) : Checksum(0x81, 0x8a, 0x8b);
    public byte[] Power(bool on)
    {
        byte v = on ? (byte)0x23 : (byte)0x24;
        if (Kind is ProtocolKind.Legacy or ProtocolKind.LegacyRgbw) return new byte[] { 0xcc, v, 0x33 };
        byte[] inner = Checksum(0x71, v, 0x0f);
        return Kind is ProtocolKind.Spi3 or ProtocolKind.Hsv25 ? Wrap(inner) : inner;
    }
    public byte[] Color(Rgb rgb, bool persist)
    {
        byte mask = WriteAll ? (byte)0 : (byte)0xf0, head = persist ? (byte)0x31 : (byte)0x41;
        return Kind switch
        {
            ProtocolKind.Legacy => new byte[] { 0x56, rgb.R, rgb.G, rgb.B, 0xaa },
            ProtocolKind.LegacyRgbw => new byte[] { 0x56, rgb.R, rgb.G, rgb.B, 0, mask, 0xaa },
            ProtocolKind.Rgb9 => Checksum(head, rgb.R, rgb.G, rgb.B, 0, 0, mask, 0x0f),
            ProtocolKind.Spi2 => Spi(rgb),
            ProtocolKind.Spi3 => Wrap(Spi(rgb)),
            ProtocolKind.Hsv25 => HsvColor(rgb),
            _ => Checksum(head, rgb.R, rgb.G, rgb.B, 0, mask, 0x0f)
        };
    }
    static byte[] Spi(Rgb c) => Checksum(0x41, 1, c.R, c.G, c.B, 0, 0, 0, 0x60, 0xff, 0, 0);
    byte[] HsvColor(Rgb c)
    {
        double r = c.R / 255d, g = c.G / 255d, b = c.B / 255d, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? ((g - b) / d % 6) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        if (h < 0) h += 6;
        return Wrap(new byte[] { 0xe0, 1, 0, 0xa1, (byte)(h * 30), (byte)(max == 0 ? 0 : d / max * 100), (byte)(max * 100), 0, 0, 0, 0, 0x14, 0, 0 }, 2);
    }
    public static byte[]? ExtractState(byte[] response)
    {
        for (int i = 0; i < response.Length; i++)
        {
            if (response[i] == 0x81 && i + 14 <= response.Length)
            {
                var state = response.Skip(i).Take(14).ToArray();
                if (unchecked((byte)state.Take(13).Sum(x => x)) == state[13]) return state;
            }
            if (response[i] == 0x66 && i + 11 <= response.Length) return response.Skip(i).Take(11).ToArray();
            if (response[i] == 0xea && i + 20 <= response.Length && response[i + 1] == 0x81)
            {
                byte[] state = new byte[14]; state[0] = 0x81; state[1] = response[i + 4]; state[10] = response[i + 5]; return state;
            }
        }
        return null;
    }
    public static MagicProtocol ForState(byte[] state)
    {
        if (state[0] == 0x66) return new(state[1] == 0x04 ? ProtocolKind.LegacyRgbw : ProtocolKind.Legacy);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MagicHomeNET.Models.json")!;
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty(state[1].ToString(), out var model)) return new(ProtocolKind.Rgb8, false);
        string name = "";
        foreach (var rule in model.GetProperty("protocols").EnumerateArray())
            if (state[10] >= rule.GetProperty("min").GetInt32()) { name = rule.GetProperty("name").GetString()!; break; }
        ProtocolKind kind = name.Contains("25_BYTE") ? ProtocolKind.Hsv25 : name.Contains("ADDRESSABLE_A3") ? ProtocolKind.Spi3 : name.Contains("ADDRESSABLE_A2") ? ProtocolKind.Spi2 : name.Contains("ADDRESSABLE_A1") || name is "LEDENET" or "LEDENET_AUTO_ON" or "LEDENET_DIMMABLE_EFFECTS" ? ProtocolKind.Rgb9 : ProtocolKind.Rgb8;
        return new(kind, model.GetProperty("all").GetBoolean());
    }
}
public interface ILightConnection : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken token);
    Task SetColorAsync(Rgb color, bool persist, CancellationToken token);
    Task SetPowerAsync(bool on, CancellationToken token);
    Task PingAsync(CancellationToken token);
    string Description { get; }
}
public sealed class TcpLightConnection : ILightConnection
{
    readonly string host;
    readonly int port;
    readonly ProtocolKind requested;
    TcpClient? client;
    MagicProtocol protocol = new();
    public string Description => protocol.Kind.ToString();
    public TcpLightConnection(string host, ProtocolKind requested, int port = 5577) { this.host = host; this.port = port; this.requested = requested; }
    public async Task ConnectAsync(CancellationToken token)
    {
        if (requested != ProtocolKind.Auto)
        {
            client = await Open(token); protocol = new(requested); return;
        }
        foreach (var candidate in new[] { ProtocolKind.Rgb8, ProtocolKind.Legacy, ProtocolKind.Spi3 })
        {
            client?.Dispose(); client = await Open(token);
            var probe = new MagicProtocol(candidate);
            await client.GetStream().WriteAsync(probe.Query(), token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(800);
            using var received = new MemoryStream();
            try
            {
                byte[] buffer = new byte[256];
                while (received.Length < 1024)
                {
                    int count = await client.GetStream().ReadAsync(buffer, timeout.Token);
                    if (count == 0) break;
                    received.Write(buffer, 0, count);
                    if (MagicProtocol.ExtractState(received.ToArray()) is { } state)
                    {
                        protocol = MagicProtocol.ForState(state); return;
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        throw new IOException("Контроллер не ответил на запрос Magic Home. Проверьте IP или выберите протокол вручную.");
    }
    async Task<TcpClient> Open(CancellationToken token)
    {
        var connection = new TcpClient { NoDelay = true };
        try { await connection.ConnectAsync(host, port, token).AsTask().WaitAsync(TimeSpan.FromSeconds(2), token); return connection; }
        catch { connection.Dispose(); throw; }
    }
    public Task SetColorAsync(Rgb color, bool persist, CancellationToken token) => Write(protocol.Color(color, persist), token);
    public Task SetPowerAsync(bool on, CancellationToken token) => Write(protocol.Power(on), token);
    public async Task PingAsync(CancellationToken token)
    {
        if (client == null) throw new IOException("Нет подключения.");
        byte[] pending = new byte[1024];
        for (int i = 0; i < 16 && client.Available > 0; i++)
        {
            int drained = await client.GetStream().ReadAsync(pending.AsMemory(0, Math.Min(pending.Length, client.Available)), token);
            if (drained == 0) throw new IOException("Контроллер закрыл соединение.");
        }
        await Write(protocol.Query(), token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(1000);
        using var received = new MemoryStream();
        byte[] bytes = new byte[512];
        while (received.Length < 8192)
        {
            int count = await client!.GetStream().ReadAsync(bytes, timeout.Token);
            if (count == 0) throw new IOException("Контроллер закрыл соединение.");
            received.Write(bytes, 0, count);
            if (MagicProtocol.ExtractState(received.ToArray()) != null) return;
        }
        throw new IOException("Нет ответа на проверку состояния.");
    }
    async Task Write(byte[] bytes, CancellationToken token)
    {
        if (client is null) throw new IOException("Нет подключения.");
        await client.GetStream().WriteAsync(bytes, token).AsTask().WaitAsync(TimeSpan.FromSeconds(1), token);
    }
    public ValueTask DisposeAsync() { client?.Dispose(); return ValueTask.CompletedTask; }
}
public sealed class LightClient : IAsyncDisposable
{
    readonly ILightConnection connection;
    readonly Action<string> status;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim ready = new(0, 1);
    readonly object sync = new();
    (Rgb Color, bool Persist)? frame;
    bool? power;
    bool? desiredPower;
    long revision;
    readonly Task worker;
    readonly int heartbeatMs, retryMs;
    public LightClient(ILightConnection connection, Action<string> status, int heartbeatMs = 5000, int retryMs = 2000) { this.connection = connection; this.status = status; this.heartbeatMs = heartbeatMs; this.retryMs = retryMs; worker = Task.Run(Run); }
    void Wake() { if (ready.CurrentCount == 0) { try { ready.Release(); } catch (SemaphoreFullException) { } } }
    public void Publish(Rgb color, bool persist) { lock (sync) { frame = (color, persist); revision++; } Wake(); }
    public void Power(bool on) { lock (sync) { power = desiredPower = on; if (!on) frame = null; } Wake(); }
    async Task Run()
    {
        try
        {
            int failures = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    status(failures == 0 ? "Подключение…" : "Переподключение…");
                    await connection.ConnectAsync(stop.Token);
                    status("Подключён · " + connection.Description);
                    failures = 0;
                    long sent = -1; bool powered = true;
                    lock (sync) { power = desiredPower; }
                    Wake();
                    long checkedAt = Environment.TickCount64;
                    while (!stop.IsCancellationRequested)
                    {
                        await ready.WaitAsync(TimeSpan.FromMilliseconds(Math.Min(1000, heartbeatMs)), stop.Token);
                        (Rgb Color, bool Persist)? current; bool? p; long version;
                        lock (sync) { current = frame; p = power; power = null; version = revision; }
                        if (p.HasValue) { await connection.SetPowerAsync(p.Value, stop.Token); powered = p.Value; status(powered ? "Включён" : "Выключен"); }
                        if (powered && current.HasValue && (version != sent || p == true)) await connection.SetColorAsync(current.Value.Color, current.Value.Persist, stop.Token);
                        sent = version;
                        if (Environment.TickCount64 - checkedAt >= heartbeatMs)
                        {
                            await connection.PingAsync(stop.Token);
                            checkedAt = Environment.TickCount64;
                        }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    await connection.DisposeAsync();
                    int delay = Math.Min(15000, retryMs * (1 << Math.Min(failures++, 3)));
                    status($"Нет связи · повтор через {Math.Max(1, delay / 1000)} с");
                    App.Log(new IOException("Контроллер: " + connection.Description + " · " + ex.Message));
                    await Task.Delay(delay, stop.Token);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { status("Ошибка: " + ex.Message); }
        finally { await connection.DisposeAsync(); }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); await connection.DisposeAsync(); try { await worker.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } stop.Dispose(); }
}
public sealed class LightGroup : IAsyncDisposable
{
    readonly Func<DeviceEntry, ILightConnection> factory;
    readonly SemaphoreSlim lifecycle = new(1, 1);
    public LightGroup(Func<DeviceEntry, ILightConnection>? factory = null) { this.factory = factory ?? ConnectionFactory.Create; }
    LightClient[] clients = Array.Empty<LightClient>();
    public int Count => Volatile.Read(ref clients).Length;
    public async Task ConnectAsync(IEnumerable<DeviceEntry> devices, Action<string, string> status)
    {
        await lifecycle.WaitAsync();
        try
        {
            await ClearClients();
            var next = new List<LightClient>();
            foreach (var d in devices.DistinctBy(d => d.Ip))
            {
                string ip = d.Ip;
                next.Add(new(factory(d), s => status(ip, s)));
            }
            Volatile.Write(ref clients, next.ToArray());
        }
        finally { lifecycle.Release(); }
    }
    public void Publish(Rgb color, bool persist = false) { foreach (var client in Volatile.Read(ref clients)) client.Publish(color, persist); }
    public void Power(bool on) { foreach (var client in Volatile.Read(ref clients)) client.Power(on); }
    async Task ClearClients() { var old = Interlocked.Exchange(ref clients, Array.Empty<LightClient>()); await Task.WhenAll(old.Select(c => c.DisposeAsync().AsTask())); }
    public async ValueTask DisposeAsync() { await lifecycle.WaitAsync(); try { await ClearClients(); } finally { lifecycle.Release(); } }
}
public static class DeviceDiscovery
{
    public static async Task<IReadOnlyList<DeviceEntry>> ScanAsync(CancellationToken token)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        try { udp.Client.Bind(new IPEndPoint(IPAddress.Any, 48899)); } catch (SocketException) { udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0)); }
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up))
            foreach (var addr in adapter.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && x.IPv4Mask != null))
            {
                var ip = addr.Address.GetAddressBytes(); var mask = addr.IPv4Mask.GetAddressBytes();
                addresses.Add(new IPAddress(ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray()));
            }
        var found = new Dictionary<string, DeviceEntry>();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(5));
        async Task Send()
        {
            for (int round = 0; round < 3; round++)
            {
                foreach (var address in addresses) { try { await udp.SendAsync(Encoding.ASCII.GetBytes("HF-A11ASSISTHREAD"), new IPEndPoint(address, 48899), limit.Token); } catch (SocketException) { } }
                await Task.Delay(1300, limit.Token);
            }
        }
        var sending = Send();
        try
        {
            while (!limit.IsCancellationRequested)
            {
                var response = await udp.ReceiveAsync(limit.Token);
                var parts = Encoding.ASCII.GetString(response.Buffer).Trim().Split(',');
                if (parts.Length < 3 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork) continue;
                // Use the sender address rather than trusting an advertised host.
                string ip = response.RemoteEndPoint.Address.ToString();
                found[ip] = new() { Ip = ip, Name = parts[1] + " · " + parts[2] };
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        finally { try { await sending; } catch (OperationCanceledException) { } }
        token.ThrowIfCancellationRequested(); return found.Values.OrderBy(x => x.Ip).ToArray();
    }
}
