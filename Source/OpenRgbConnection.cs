using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MagicHomeNET;

public record PcZone(string Name, int Leds);
public record PcController(int Index, string Name, string Vendor, string Serial, string Location,
    int Colors, bool Direct, PcZone[] Zones)
{
    public string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", Name, Vendor, Serial, Location))));
}

// Negotiate SDK v3, also supported by newer servers: their v4-v6 fields are not requested.
public sealed class OpenRgbSession : IAsyncDisposable
{
    const int MaxPacket = 1024 * 1024;
    TcpClient? tcp;
    CancellationTokenSource? stop;
    Task? pump;
    Channel<(uint Device, uint Id, byte[] Data)> replies = null!;
    volatile bool changed;
    public bool Changed => changed;
    uint version;

    public async Task Connect(string host, int port, CancellationToken token)
    {
        tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, token).AsTask().WaitAsync(TimeSpan.FromSeconds(3), token);
        stop = new();
        replies = Channel.CreateBounded<(uint, uint, byte[])>(32);
        pump = ReadLoop(stop.Token);
        var protocol = await Request(0, 40, Number(3), token);
        if (protocol.Length != 4) throw new IOException("Некорректная версия SDK OpenRGB.");
        version = Math.Min(3, BinaryPrimitives.ReadUInt32LittleEndian(protocol));
        if (version < 1) throw new IOException("Требуется OpenRGB с SDK 1 или новее.");
        await Send(0, 50, Encoding.UTF8.GetBytes("RGBroom\0"), token);
    }

    async Task ReadLoop(CancellationToken token)
    {
        Exception? error = null;
        try
        {
            while (true)
            {
                var header = new byte[16];
                await tcp!.GetStream().ReadExactlyAsync(header, token);
                if (!header.AsSpan(0, 4).SequenceEqual("ORGB"u8)) throw new IOException("Неверный заголовок OpenRGB.");
                uint device = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                if (size > MaxPacket) throw new IOException("Слишком большой пакет OpenRGB.");
                var data = new byte[(int)size];
                await tcp.GetStream().ReadExactlyAsync(data, token);
                if (id == 100) { changed = true; continue; }
                if (id is 0 or 1 or 40) await replies.Writer.WriteAsync((device, id, data), token);
            }
        }
        catch (Exception ex) { error = ex; }
        finally { replies.Writer.TryComplete(error); }
    }

    public static byte[] Number(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); return b; }
    public async Task Send(uint device, uint id, byte[] data, CancellationToken token)
    {
        if (Changed) throw new IOException("Список компонентов OpenRGB изменился; требуется повторное обнаружение.");
        var bytes = new byte[16 + data.Length];
        "ORGB"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), device);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)data.Length);
        data.CopyTo(bytes, 16);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(2500);
        await tcp!.GetStream().WriteAsync(bytes, timeout.Token);
    }

    async Task<byte[]> Request(uint device, uint id, byte[] data, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(3000);
        await Send(device, id, data, timeout.Token);
        var response = await replies.Reader.ReadAsync(timeout.Token);
        if (Changed || response.Id != id || response.Device != device) throw new IOException("Неожиданный ответ OpenRGB.");
        return response.Data;
    }

    public async Task<int> Count(CancellationToken token)
    {
        var data = await Request(0, 0, Array.Empty<byte>(), token);
        if (data.Length != 4) throw new IOException("Некорректный список OpenRGB.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count > 256) throw new IOException("Слишком много компонентов OpenRGB.");
        return (int)count;
    }

    public async Task<PcController[]> Controllers(CancellationToken token)
    {
        int count = await Count(token);
        var devices = new List<PcController>();
        for (int i = 0; i < count; i++)
            devices.Add(Parse(i, await Request((uint)i, 1, Number(version), token), version));
        return devices.ToArray();
    }

    public static PcController Parse(int index, byte[] data, uint version)
    {
        using var stream = new MemoryStream(data, false);
        using var r = new BinaryReader(stream, Encoding.UTF8);
        string Text()
        {
            int length = r.ReadUInt16(); var bytes = r.ReadBytes(length);
            if (length == 0 || bytes.Length != length || bytes[^1] != 0) throw new IOException("Некорректная строка OpenRGB.");
            return Encoding.UTF8.GetString(bytes, 0, length - 1);
        }
        void Skip(int length) { if (length < 0 || length > stream.Length - stream.Position) throw new EndOfStreamException(); stream.Position += length; }
        if (version is < 1 or > 3 || r.ReadUInt32() != data.Length) throw new IOException("Некорректные данные OpenRGB.");
        r.ReadInt32(); string name = Text(), vendor = Text(); Text(); Text(); string serial = Text(), location = Text();
        int modes = r.ReadUInt16(); r.ReadInt32(); bool direct = false;
        for (int i = 0; i < modes; i++)
        {
            string mode = Text();
            if (mode.Equals("Direct", StringComparison.OrdinalIgnoreCase)) direct = true;
            Skip(version >= 3 ? 48 : 36);
            Skip(checked(r.ReadUInt16() * 4));
        }
        int zones = r.ReadUInt16(); var list = new List<PcZone>();
        for (int i = 0; i < zones; i++)
        {
            string zone = Text(); Skip(12); uint count = r.ReadUInt32();
            if (count > 65535) throw new IOException("Слишком большая зона OpenRGB.");
            Skip(r.ReadUInt16()); list.Add(new(zone, (int)count));
        }
        int leds = r.ReadUInt16();
        for (int i = 0; i < leds; i++) { Text(); Skip(4); }
        int colors = r.ReadUInt16(); Skip(colors * 4);
        if (stream.Position != stream.Length) throw new IOException("Лишние данные OpenRGB.");
        return new(index, name, vendor, serial, location, colors, direct, list.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        stop?.Cancel(); tcp?.Dispose();
        if (pump != null) await pump;
        stop?.Dispose(); stop = null; tcp = null; pump = null;
    }
}

public sealed class OpenRgbConnection : ILightConnection
{
    readonly DeviceEntry entry;
    OpenRgbSession? session;
    PcController? controller;
    int zone, colorCount;
    Rgb last = new(255, 255, 255);
    long sentAt;
    public string Description => "OpenRGB · " + (controller?.Name ?? "подсветка ПК");
    public OpenRgbConnection(DeviceEntry entry) { this.entry = entry; }
    public static bool Overlaps(IEnumerable<DeviceEntry> devices) => devices.Where(d => d.Protocol == ProtocolKind.OpenRgb)
        .Select(d => TryAddress(d.Ip, out var u) ? u : null).Where(u => u != null)
        .GroupBy(u => u!.Authority + "/" + u.AbsolutePath.Split('/')[1])
        .Any(g => g.Count() > 1 && g.Any(u => u!.AbsolutePath.EndsWith("/-1", StringComparison.Ordinal)));
    public static string Address(string host, int port, PcController d, int zone) => $"openrgb://{host}:{port}/{d.Identity}/{zone}";
    public static bool TryAddress(string value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var result) || result.Scheme != "openrgb" || result.Port is < 1 or > 65535 || result.UserInfo.Length != 0 || result.Query.Length != 0 || result.Fragment.Length != 0) return false;
        var path = result.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length != 2 || path[0].Length != 64 || !path[0].All(Uri.IsHexDigit) || !int.TryParse(path[1], out int z) || z < -1 || z > 65535) return false;
        uri = result; return true;
    }
    public static async Task<PcController[]> Discover(string host, int port, CancellationToken token)
    {
        await using var s = new OpenRgbSession(); await s.Connect(host, port, token); return await s.Controllers(token);
    }
    public async Task ConnectAsync(CancellationToken token)
    {
        await DisposeAsync();
        if (!TryAddress(entry.Ip, out var address)) throw new IOException("Добавьте компонент через окно «Подсветка ПК».");
        session = new(); await session.Connect(address.Host, address.Port, token);
        var path = address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var matches = (await session.Controllers(token)).Where(d => d.Identity == path[0]).ToArray();
        if (matches.Length != 1) throw new IOException("Компонент OpenRGB отсутствует или его идентификатор неоднозначен. Повторите поиск.");
        controller = matches[0]; zone = int.Parse(path[1]);
        if (!controller.Direct) throw new IOException("Компонент не поддерживает режим Direct.");
        if (zone >= controller.Zones.Length || (zone >= 0 && controller.Zones[zone].Name != entry.OpenRgbZoneName)) throw new IOException("Зоны компонента изменились. Повторите поиск.");
        colorCount = zone < 0 ? controller.Colors : controller.Zones[zone].Leds;
        if (colorCount <= 0) throw new IOException("В выбранной зоне нет настроенных светодиодов.");
        await session.Send((uint)controller.Index, 1100, Array.Empty<byte>(), token);
    }
    public async Task PingAsync(CancellationToken token)
    {
        if (session == null || controller == null) throw new IOException("Нет связи с OpenRGB.");
        var devices = await session.Controllers(token);
        if (controller.Index >= devices.Length || devices[controller.Index] != controller && !Same(devices[controller.Index], controller))
            throw new IOException("Список компонентов OpenRGB изменился.");
    }
    static bool Same(PcController a, PcController b) => a.Identity == b.Identity && a.Colors == b.Colors && a.Direct == b.Direct && a.Zones.SequenceEqual(b.Zones);
    public static byte[] ColorPacket(Rgb color, int count, int zone)
    {
        if (count is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(count));
        var data = new byte[(zone < 0 ? 6 : 10) + count * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
        int start = 4;
        if (zone >= 0) { BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(start), (uint)zone); start += 4; }
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(start), (ushort)count); start += 2;
        for (int i = start; i < data.Length; i += 4) { data[i] = color.R; data[i + 1] = color.G; data[i + 2] = color.B; }
        return data;
    }
    async Task Write(Rgb color, CancellationToken token)
    {
        long wait = 33 - (Environment.TickCount64 - sentAt);
        if (wait > 0) await Task.Delay((int)wait, token);
        await session!.Send((uint)controller!.Index, zone < 0 ? 1050u : 1051u, ColorPacket(color, colorCount, zone), token);
        sentAt = Environment.TickCount64;
    }
    public async Task SetColorAsync(Rgb color, bool persist, CancellationToken token) { last = color; await Write(color, token); }
    public Task SetPowerAsync(bool on, CancellationToken token) => Write(on ? last : Rgb.Black, token);
    public async ValueTask DisposeAsync() { if (session != null) await session.DisposeAsync(); session = null; }
}
