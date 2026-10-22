using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MagicHomeNET;
public static class SelfTests
{
    public static bool Active { get; private set; }
    public static void Report(string message)
    {
        string path = Environment.GetEnvironmentVariable("MAGICHOME_TEST_REPORT") ?? Path.Combine(AppContext.BaseDirectory, "test-report.txt");
        File.AppendAllText(path, message + Environment.NewLine);
    }
    public static void Assert(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    static async Task Until(Func<bool> predicate, string message, int timeout = 4000)
    {
        long end = Environment.TickCount64 + timeout;
        while (Environment.TickCount64 < end) { if (predicate()) return; await Task.Delay(10); }
        Assert(false, "Timeout: " + message);
    }
    static EngineOptions Options(LightMode mode = LightMode.Music, MusicStyle style = MusicStyle.Strobe) => new(mode, new(255, 0, 0), 65, 12, style, 8, new[] { 1d, 1, 1 }, 100, 70, "", "", "");
    static float[][] Tone(double frequency, double amplitude = .2)
    {
        var left = Enumerable.Range(0, SpectrumAnalyzer.Size).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / 48000))).ToArray();
        return new[] { left, left.Select(v => -v).ToArray() };
    }
    public static async Task Run()
    {
        Active = true;
        await AdapterTests.Run();
        double[][] bands = new[] { 100d, 1000, 6000 }.Select(f => SpectrumAnalyzer.Analyze(Tone(f), 48000)).ToArray();
        for (int i = 0; i < 3; i++)
        {
            Assert(Array.IndexOf(bands[i], bands[i].Max()) == i, "FFT dominant band");
            Assert(Math.Abs(bands[i][i] - .2 / Math.Sqrt(2)) < .004, "FFT preserves opposite-phase stereo energy");
        }
        foreach (MusicStyle style in Enum.GetValues<MusicStyle>())
        {
            for (int i = 0; i < 3; i++)
            {
                var rgb = new MusicEffect().Process(bands[i], Options(LightMode.Spectrum, style), .025);
                var channels = new[] { rgb.R, rgb.G, rgb.B };
                Assert(Array.IndexOf(channels, channels.Max()) == new[] { 0, 2, 1 }[i], "Spectrum frequency to RGB mapping: " + style);
                var weights = new[] { 1d, 1, 1 }; weights[i] = 0;
                Assert(new MusicEffect().Process(bands[i], Options(LightMode.Spectrum, style) with { Weights = weights }, .025) == Rgb.Black, "EQ rejects disabled frequency");
            }
            var effect = new MusicEffect();
            Assert(effect.Process(bands[0], Options(style: style), .025) != Rgb.Black, "All music styles respond");
            Rgb dark = Rgb.Black;
            for (int i = 0; i < 160; i++) dark = effect.Process(new double[3], Options(style: style), .025);
            Assert(dark == Rgb.Black, "Silence decays to black");
        }
        foreach (int period in new[] { 6, 8, 10 })
        {
            var effect = new MusicEffect(); var flashes = new List<Rgb>();
            for (int frame = 0; frame < period * 16; frame++)
            {
                var color = effect.Process(frame % period == 0 ? bands[0] : new double[3], Options(), .025);
                if (frame % period == 0) { flashes.Add(color); Assert(new[] { color.R, color.G, color.B }.Max() == 255, "Immediate full-bright strobe on rapid beats"); }
            }
            Assert(flashes.Distinct().Count() == 6, "Strobe cycles six colors");
        }
        Report("PASS: native C# FFT, stereo energy, frequency EQ, four styles, rapid-beat colored strobe, spectrum mapping and silence");
        byte[] pixels = new byte[100 * 100 * 4];
        for (int i = 0; i < 10000; i++) { pixels[i * 4 + 2] = i < 7500 ? (byte)255 : (byte)0; pixels[i * 4 + 1] = i >= 7500 ? (byte)255 : (byte)0; }
        Assert(AmbientEffect.Dominant(pixels, 100, 100, 400) == new Rgb(255, 0, 0), "Dominant color is not averaged");
        var ambient = new AmbientEffect(); ambient.Process(new(255, 0, 0), .08); var blend = ambient.Process(new(0, 0, 255), .08);
        Assert(blend.R > 0 && blend.B > 0 && blend.R < 255, "Ambient smoothing");
        Assert(new Rgb(255, 20, 0).Scale(.5) == new Rgb(128, 10, 0), "Brightness scaling");
        string legacy = "{\"brightness\":27,\"transition_brightness\":54,\"rgb\":[255,0,80],\"devices\":[{\"ip\":\"192.0.2.5\",\"checked\":true}],\"music\":{\"gain\":19,\"style\":\"Стробоскоп\",\"eq\":[150,0,0],\"output\":\"Наушники\"},\"spectrum\":{\"gain\":4},\"display\":\"Основной дисплей\"}";
        var imported = SettingsStore.ImportLegacy(legacy);
        Assert(imported.Music.Gain == 19 && imported.Spectrum.Gain == 4 && imported.Music.Style == MusicStyle.Strobe && imported.Music.Low == 150, "Legacy profile import");
        var roundTrip = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(imported))!;
        Assert(roundTrip.Brightness == 27 && roundTrip.TransitionBrightness == 54 && roundTrip.Devices[0].Ip == "192.0.2.5" && roundTrip.Color == new Rgb(255, 0, 80), "Settings roundtrip");
        Report("PASS: ambient dominant color/smoothing, brightness and migration of Python settings to independent .NET profiles");
        var protocol = new MagicProtocol();
        Assert(Convert.ToHexString(protocol.Query()) == "818A8B96", "Query bytes");
        Assert(Convert.ToHexString(protocol.Color(new(255, 0, 0), true)) == "31FF000000000F3F", "RGB8 color checksum");
        Assert(Convert.ToHexString(protocol.Power(true)) == "71230FA3", "Power checksum");
        Assert(new MagicProtocol(ProtocolKind.Rgb9).Color(new(1, 2, 3), false).Length == 9, "RGB9 length");
        Assert(Convert.ToHexString(new MagicProtocol(ProtocolKind.Legacy).Color(new(1, 2, 3), false)) == "56010203AA", "Legacy color");
        var wrapped = new MagicProtocol(ProtocolKind.Spi3).Color(new(1, 2, 3), false);
        Assert(wrapped.Length == 24 && wrapped[10] == 0x41 && unchecked((byte)wrapped.Take(wrapped.Length - 1).Sum(x => x)) == wrapped[^1], "SPI wrapper checksums");
        await TcpProtocolTest();
        Report("PASS: protocol encoding, checksums, legacy/SPI formats and real loopback TCP query/color/power/heartbeat with fragmented responses");
        await ReconnectionTest();
        Report("PASS: heartbeat detects disconnect without color changes, automatic reconnect, latest frame replay, power-off restoration and independent healthy controller");
        await EngineTest();
        Report("PASS: C# mode engine, mock audio/display sources, ambient output, brightness, source switching, pause and resume");
        Report("ALL TESTS PASSED");
    }
    static async Task TcpProtocolTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var packets = new ConcurrentQueue<byte[]>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(stop.Token); var stream = peer.GetStream();
            while (!stop.IsCancellationRequested)
            {
                int head = stream.ReadByte(); if (head < 0) break;
                int length = head == 0x81 || head == 0x71 ? 4 : 8;
                byte[] packet = new byte[length]; packet[0] = (byte)head; await stream.ReadExactlyAsync(packet.AsMemory(1), stop.Token); packets.Enqueue(packet);
                if (head == 0x81)
                {
                    byte[] state = MagicProtocol.Checksum(0x81, 0x33, 0x23, 0x61, 0, 0x10, 0xff, 0, 0, 0, 9, 0, 0);
                    await stream.WriteAsync(state.AsMemory(0, 5), stop.Token); await Task.Delay(15, stop.Token); await stream.WriteAsync(state.AsMemory(5), stop.Token);
                }
            }
        });
        await using (var connection = new TcpLightConnection("127.0.0.1", ProtocolKind.Auto, port))
        {
            await connection.ConnectAsync(stop.Token); Assert(connection.Description == "Rgb8", "Auto detects RGB controller");
            await connection.SetColorAsync(new(255, 0, 0), true, stop.Token); await connection.SetPowerAsync(false, stop.Token); await connection.PingAsync(stop.Token);
        }
        await server; listener.Stop();
        Assert(packets.Count >= 4 && packets.Any(p => p[0] == 0x31) && packets.Any(p => p[0] == 0x71), "TCP commands reached emulator");
    }
    sealed class FakeConnection : ILightConnection
    {
        public bool Offline;
        public int Connections, Pings;
        public string Description => "Test controller";
        public ConcurrentQueue<Rgb> Colors = new();
        public ConcurrentQueue<bool> Powers = new();
        public Task ConnectAsync(CancellationToken token) { Interlocked.Increment(ref Connections); if (Offline) throw new IOException("offline"); return Task.CompletedTask; }
        public Task SetColorAsync(Rgb c, bool persist, CancellationToken token) { if (Offline) throw new IOException("offline"); Colors.Enqueue(c); return Task.CompletedTask; }
        public Task SetPowerAsync(bool on, CancellationToken token) { if (Offline) throw new IOException("offline"); Powers.Enqueue(on); return Task.CompletedTask; }
        public Task PingAsync(CancellationToken token) { Interlocked.Increment(ref Pings); if (Offline) throw new IOException("offline"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    static async Task ReconnectionTest()
    {
        var source = new FakeConnection(); var healthy = new FakeConnection();
        var statuses = new ConcurrentQueue<string>();
        await using var client = new LightClient(source, statuses.Enqueue, 60, 80);
        await using var other = new LightClient(healthy, _ => { }, 60, 80);
        client.Publish(new(255, 0, 0), false); other.Publish(new(0, 255, 0), false);
        await Until(() => source.Colors.Count > 0 && healthy.Colors.Count > 0, "initial frames");
        source.Offline = true;
        await Until(() => statuses.Any(s => s.StartsWith("Нет связи")), "heartbeat disconnect detection");
        client.Publish(new(0, 0, 255), false); client.Power(false);
        other.Publish(new(0, 255, 255), false); await Until(() => healthy.Colors.Contains(new(0, 255, 255)), "healthy client keeps working");
        source.Offline = false;
        await Until(() => source.Connections >= 2 && source.Powers.Contains(false), "reconnect restores off state");
        int count = source.Colors.Count; await Task.Delay(100); Assert(source.Colors.Count == count, "Reconnect does not relight disabled strip");
        client.Power(true); client.Publish(new(0, 0, 255), false);
        await Until(() => source.Colors.Contains(new(0, 0, 255)), "latest color after power on");
        source.Offline = true; await Until(() => statuses.Count(s => s.StartsWith("Нет связи")) >= 2, "second disconnect");
        client.Publish(new(10, 20, 30), false); client.Publish(new(70, 80, 90), false); source.Offline = false;
        await Until(() => source.Colors.Contains(new(70, 80, 90)), "latest frame replay on reconnect");
        Assert(source.Pings > 0 && healthy.Pings > 0, "Periodic checks on both clients");
    }
    sealed class FakeAudio : IAudioSource
    {
        public int SampleRate => 48000;
        public bool Disposed;
        public float[][] Snapshot() => Tone(100);
        public void Dispose() => Disposed = true;
    }
    sealed class FakeScreen : IScreenSource
    {
        public int Reads;
        public bool Disposed;
        readonly Rgb color;
        public FakeScreen(Rgb color) { this.color = color; }
        public Rgb ReadDominant() { Reads++; return color; }
        public void Dispose() => Disposed = true;
    }
    static async Task EngineTest()
    {
        var source = new FakeConnection();
        await using var group = new LightGroup(_ => source);
        await group.ConnectAsync(new[] { new DeviceEntry { Ip = "192.0.2.5" } }, (_, _) => { });
        var screens = new List<FakeScreen>(); var audios = new List<FakeAudio>();
        await using var engine = new LightEngine(group, (_, _) => { }, text => throw new Exception(text), (id, name) => { var a = new FakeAudio(); audios.Add(a); return a; }, id => { var s = new FakeScreen(id == "second" ? new(0, 0, 255) : new(255, 0, 0)); screens.Add(s); return s; });
        engine.Update(Options(LightMode.Ambient) with { Brightness = 50 });
        await Until(() => source.Colors.Contains(new(128, 0, 0)), "ambient brightness");
        engine.Update(Options(LightMode.Ambient) with { Brightness = 50, Display = "second" });
        await Until(() => source.Colors.Contains(new(0, 0, 128)), "switch monitor"); Assert(screens[0].Disposed, "old screen released");
        engine.Power(false); await Until(() => screens[^1].Disposed, "power off stops capture");
        engine.Power(true); await Until(() => screens.Count >= 3, "power on restarts capture");
        engine.Update(Options(LightMode.Spectrum)); await Until(() => audios.Count > 0, "audio mode starts source");
        engine.Update(Options(LightMode.Color)); await Until(() => audios[0].Disposed, "leaving music releases capture");
    }
}
