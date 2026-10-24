namespace MagicHomeNET;

public record AudioEndpoint(string Id, string Name) { public override string ToString() => Name; }
public record DisplayEndpoint(string Id, string Name) { public override string ToString() => Name; }
public interface IAudioSource : IDisposable { int SampleRate { get; } float[][] Snapshot(); }
public interface IScreenSource : IDisposable { Rgb ReadDominant(); }

public sealed class AudioSource : IAudioSource
{
    readonly int handle;
    bool disposed;
    public int SampleRate => 48000;
    public static IReadOnlyList<AudioEndpoint> Outputs() => new[] { new AudioEndpoint("", "Системный звук macOS · все приложения") };
    public AudioSource(string id, string oldName) => handle = Native.Call(new { op = "capture-start", audio = true, display = "" }).GetProperty("handle").GetInt32();
    public float[][] Snapshot()
    {
        var samples = new float[SpectrumAnalyzer.Size * 2];
        if (Native.rgb_audio(handle, samples) < 0) throw new IOException("Захват звука остановлен. Проверьте разрешение записи экрана и системного аудио.");
        return new[] { samples[..SpectrumAnalyzer.Size], samples[SpectrumAnalyzer.Size..] };
    }
    public void Dispose() { if (!disposed) { disposed = true; Native.Call(new { op = "capture-stop", handle }); } }
}

public sealed class ScreenSource : IScreenSource
{
    readonly int handle;
    readonly byte[] pixels = new byte[128 * 72 * 4];
    bool disposed;
    public static IReadOnlyList<DisplayEndpoint> Displays() => Native.Call(new { op = "displays" }).GetProperty("displays").EnumerateArray()
        .Select(d => new DisplayEndpoint(d.GetProperty("id").GetString()!, d.GetProperty("name").GetString()!)).ToArray();
    public ScreenSource(string id) => handle = Native.Call(new { op = "capture-start", audio = false, display = id }).GetProperty("handle").GetInt32();
    public Rgb ReadDominant()
    {
        if (Native.rgb_screen(handle, pixels) < 0) throw new IOException("Захват экрана остановлен. Проверьте разрешение записи экрана.");
        return AmbientEffect.Dominant(pixels, 128, 72, 128 * 4);
    }
    public void Dispose() { if (!disposed) { disposed = true; Native.Call(new { op = "capture-stop", handle }); } }
}
