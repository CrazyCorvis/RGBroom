using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MagicHomeNET;
public static class OpenRgbTests
{
    static byte[] Description(string name, uint version = 3)
    {
        using var m = new MemoryStream(); using var w = new BinaryWriter(m, Encoding.UTF8, true);
        void Text(string s) { var b = Encoding.UTF8.GetBytes(s + "\0"); w.Write((ushort)b.Length); w.Write(b); }
        w.Write(0u); w.Write(0u); Text(name); Text("ASUS / MSI / Gigabyte / ASRock"); Text("board"); Text("1"); Text(name + "-serial"); Text("USB0");
        w.Write((ushort)1); w.Write(0); Text("Direct");
        for (int i = 0; i < (version >= 3 ? 12 : 9); i++) w.Write(0u);
        w.Write((ushort)0); w.Write((ushort)2);
        foreach (var zone in new[] { "Mainboard", "ARGB Header" })
        {
            Text(zone); w.Write(1u); w.Write(1u); w.Write(100u); w.Write(2u);
            if (zone == "Mainboard") { w.Write((ushort)12); w.Write(1u); w.Write(1u); w.Write(0u); }
            else w.Write((ushort)0);
        }
        w.Write((ushort)4);
        for (int i = 0; i < 4; i++) { Text("LED" + i); w.Write((uint)i); }
        w.Write((ushort)4); for (int i = 0; i < 4; i++) w.Write(0u);
        w.Flush(); var bytes = m.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length); return bytes;
    }
    static async Task Send(NetworkStream s, uint device, uint id, byte[] data, CancellationToken token)
    {
        var header = new byte[16]; "ORGB"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), device);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)data.Length);
        await s.WriteAsync(header.AsMemory(0, 7), token); await Task.Delay(1, token);
        await s.WriteAsync(header.AsMemory(7), token);
        if (data.Length > 0) { await s.WriteAsync(data.AsMemory(0, 1), token); await Task.Delay(1, token); await s.WriteAsync(data.AsMemory(1), token); }
    }
    public static async Task Run()
    {
        var info = OpenRgbSession.Parse(0, Description("Board"), 3);
        var older = OpenRgbSession.Parse(0, Description("Board", 1), 1);
        SelfTests.Assert(info.Direct && info.Colors == 4 && info.Zones[1].Name == "ARGB Header" && older.Colors == 4, "OpenRGB SDK 1/3 parsing including matrix zones");
        var malformed = Description("Board"); malformed[0] = 0;
        bool rejected = false; try { OpenRgbSession.Parse(0, malformed, 3); } catch (IOException) { rejected = true; }
        SelfTests.Assert(rejected, "OpenRGB invalid packet rejected");
        var whole = OpenRgbConnection.ColorPacket(new(255, 40, 10), 4, -1);
        var zone = OpenRgbConnection.ColorPacket(new(7, 8, 9), 2, 1);
        SelfTests.Assert(whole.Length == 22 && whole[6] == 255 && whole[9] == 0 && zone.Length == 18 && zone[4] == 1 && zone[10] == 7, "OpenRGB lengths, RGB byte order and zone index");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var frames = new ConcurrentQueue<(uint Device, uint Id, byte[] Data)>();
        bool changed = false; int connections = 0;
        async Task Peer(TcpClient peer)
        {
            using (peer)
            {
                int order = Interlocked.Increment(ref connections);
                try
                {
                    var stream = peer.GetStream();
                    while (!stop.IsCancellationRequested)
                    {
                        var h = new byte[16]; await stream.ReadExactlyAsync(h, stop.Token);
                        SelfTests.Assert(h.AsSpan(0, 4).SequenceEqual("ORGB"u8), "OpenRGB request magic");
                        uint dev = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)), id = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8));
                        int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12))); SelfTests.Assert(size < 1024, "OpenRGB emulator packet bound");
                        var data = new byte[size]; await stream.ReadExactlyAsync(data, stop.Token);
                        switch (id)
                        {
                            case 40: SelfTests.Assert(data.SequenceEqual(new byte[] { 3, 0, 0, 0 }), "OpenRGB negotiation"); await Send(stream, dev, id, new byte[] { 6, 0, 0, 0 }, stop.Token); break;
                            case 50: SelfTests.Assert(Encoding.UTF8.GetString(data) == "RGBroom\0", "OpenRGB client name"); break;
                            case 0:
                                if (changed) { await Send(stream, 0, 100, Array.Empty<byte>(), stop.Token); changed = false; }
                                await Send(stream, dev, id, new byte[] { 2, 0, 0, 0 }, stop.Token); break;
                            case 1:
                                SelfTests.Assert(data.SequenceEqual(new byte[] { 3, 0, 0, 0 }), "OpenRGB requests v3 layout from newer server");
                                string name = ((order == 1 && dev == 0) || (order != 1 && dev == 1)) ? "Board" : "RAM";
                                await Send(stream, dev, id, Description(name), stop.Token); break;
                            case 1050: case 1051: case 1100: frames.Enqueue((dev, id, data)); break;
                            default: throw new IOException("Unexpected OpenRGB operation " + id);
                        }
                    }
                }
                catch (Exception ex) when (ex is EndOfStreamException or OperationCanceledException or SocketException) { }
            }
        }
        var peers = new List<Task>();
        var server = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) peers.Add(Peer(await listener.AcceptTcpClientAsync(stop.Token))); }
            catch (OperationCanceledException) { }
        });
        try
        {
            var found = await OpenRgbConnection.Discover("127.0.0.1", port, stop.Token);
            SelfTests.Assert(found.Length == 2, "OpenRGB discovers multiple components");
            var d = new DeviceEntry { Protocol = ProtocolKind.OpenRgb, Ip = OpenRgbConnection.Address("127.0.0.1", port, found[0], 1), OpenRgbZoneName = "ARGB Header" };
            var other = new DeviceEntry { Protocol = ProtocolKind.OpenRgb, Ip = OpenRgbConnection.Address("127.0.0.1", port, found[1], -1) };
            var settings = new UserSettings { Devices = new() { d, other } }; settings.Validate();
            SelfTests.Assert(settings.Devices.Count == 2 && !OpenRgbConnection.Overlaps(settings.Devices), "OpenRGB multiple targets on one host survive settings validation");
            SelfTests.Assert(OpenRgbConnection.Overlaps(new[] { d, new DeviceEntry { Protocol = ProtocolKind.OpenRgb, Ip = OpenRgbConnection.Address("127.0.0.1", port, found[0], -1) } }), "OpenRGB overlapping targets rejected");
            await using var connection = new OpenRgbConnection(d);
            await connection.ConnectAsync(stop.Token);
            await connection.SetColorAsync(new(255, 40, 10), false, stop.Token);
            await connection.SetPowerAsync(false, stop.Token); await connection.SetPowerAsync(true, stop.Token);
            await connection.PingAsync(stop.Token);
            var colors = frames.Where(x => x.Id == 1051).ToArray();
            SelfTests.Assert(colors.Length == 3 && colors.All(x => x.Device == 1), "OpenRGB resolves stable identity after controller reorder");
            SelfTests.Assert(colors[0].Data[10] == 255 && colors[1].Data.Skip(10).All(x => x == 0) && colors[2].Data.SequenceEqual(colors[0].Data), "OpenRGB full brightness, black off and color restore");
            changed = true; rejected = false;
            try { await connection.PingAsync(stop.Token); } catch (IOException) { rejected = true; }
            SelfTests.Assert(rejected, "OpenRGB topology notification invalidates indices");
            await connection.ConnectAsync(stop.Token); await connection.PingAsync(stop.Token);
            SelfTests.Assert(connections == 3, "OpenRGB reconnect rediscovers controller identity");
        }
        finally { stop.Cancel(); listener.Stop(); await server; await Task.WhenAll(peers); }
        SelfTests.Report("PASS: OpenRGB SDK negotiation, fragmented TCP, component/zone discovery, stable identity after reorder, full-brightness RGB, power, heartbeat, topology invalidation, reconnect and settings");
    }
}
