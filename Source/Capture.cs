using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MagicHomeNET;
public record AudioEndpoint(string Id, string Name) { public override string ToString() => Name; }
public record DisplayEndpoint(string Id, string Name) { public override string ToString() => Name; }
public interface IAudioSource : IDisposable
{
    int SampleRate { get; }
    float[][] Snapshot();
}
public sealed class AudioSource : IAudioSource
{
    readonly MMDevice device;
    readonly WasapiLoopbackCapture capture;
    readonly object sync = new();
    readonly float[][] ring;
    int position;
    long lastData;
    volatile Exception? fault;
    volatile bool disposed;
    public int SampleRate => capture.WaveFormat.SampleRate;
    public static IReadOnlyList<AudioEndpoint> Outputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioEndpoint> { new("", "Основной выход Windows") };
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var d in devices) { result.Add(new(d.ID, d.FriendlyName)); d.Dispose(); }
        return result;
    }
    public AudioSource(string id, string oldName)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (id.Length > 0) device = enumerator.GetDevice(id);
        else if (oldName.Length > 0)
        {
            string name = oldName.Replace(" [Loopback]", "", StringComparison.OrdinalIgnoreCase);
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            string? matchingId = null;
            foreach (var candidate in devices)
            {
                if (candidate.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase) || candidate.FriendlyName.StartsWith(name, StringComparison.OrdinalIgnoreCase)) matchingId ??= candidate.ID;
                candidate.Dispose();
            }
            if (matchingId is null) throw new IOException("Сохранённый выход звука недоступен. Выберите доступный выход.");
            device = enumerator.GetDevice(matchingId);
        }
        else device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        try
        {
            capture = new WasapiLoopbackCapture(device);
            ring = Enumerable.Range(0, Math.Min(8, capture.WaveFormat.Channels)).Select(_ => new float[SpectrumAnalyzer.Size]).ToArray();
            capture.DataAvailable += Data;
            capture.RecordingStopped += (_, e) => { if (!disposed && e.Exception != null) fault = e.Exception; };
            capture.StartRecording();
        }
        catch { capture?.Dispose(); device.Dispose(); throw; }
    }
    void Data(object? sender, WaveInEventArgs e)
    {
        var format = capture.WaveFormat;
        bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        int bytes = format.BitsPerSample / 8;
        lock (sync)
        {
            for (int frame = 0; frame + format.BlockAlign <= e.BytesRecorded; frame += format.BlockAlign)
            {
                for (int ch = 0; ch < ring.Length; ch++)
                {
                    int p = frame + ch * bytes;
                    float value = floating && bytes == 4 ? BitConverter.ToSingle(e.Buffer, p) : bytes == 2 ? BitConverter.ToInt16(e.Buffer, p) / 32768f : bytes == 3 ? ((e.Buffer[p] | e.Buffer[p + 1] << 8 | e.Buffer[p + 2] << 16) << 8) / 2147483648f : bytes == 4 ? BitConverter.ToInt32(e.Buffer, p) / 2147483648f : 0;
                    ring[ch][position] = float.IsFinite(value) ? value : 0;
                }
                position = (position + 1) % SpectrumAnalyzer.Size;
            }
            lastData = Environment.TickCount64;
        }
    }
    public float[][] Snapshot()
    {
        if (fault != null) throw new IOException("Захват звука остановлен. Обновите список выходов.", fault);
        lock (sync)
        {
            var result = ring.Select(_ => new float[SpectrumAnalyzer.Size]).ToArray();
            if (Environment.TickCount64 - lastData > 150) return result;
            for (int ch = 0; ch < ring.Length; ch++)
                for (int i = 0; i < SpectrumAnalyzer.Size; i++) result[ch][i] = ring[ch][(position + i) % SpectrumAnalyzer.Size];
            return result;
        }
    }
    public void Dispose() { disposed = true; capture.StopRecording(); capture.Dispose(); device.Dispose(); }
}
public interface IScreenSource : IDisposable { Rgb ReadDominant(); }
public sealed class ScreenSource : IScreenSource
{
    readonly Rectangle bounds;
    readonly Bitmap bitmap;
    readonly Graphics graphics;
    public static IReadOnlyList<DisplayEndpoint> Displays() => new[] { new DisplayEndpoint("", "Основной дисплей") }.Concat(System.Windows.Forms.Screen.AllScreens.Select((s, i) => new DisplayEndpoint(s.DeviceName, $"Дисплей {i + 1} · {s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " · основной" : "")))).ToArray();
    public ScreenSource(string id)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var selected = id.Length == 0 || id == "Основной дисплей" ? System.Windows.Forms.Screen.PrimaryScreen : screens.FirstOrDefault(s => s.DeviceName == id);
        // Import the geometry-based monitor labels used by the Python version.
        if (selected == null) selected = screens.FirstOrDefault(s => id.Contains($"({s.Bounds.Left}, {s.Bounds.Top})"));
        if (selected == null) throw new IOException("Выбранный дисплей недоступен. Обновите список дисплеев.");
        bounds = selected.Bounds; bitmap = new(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb); graphics = Graphics.FromImage(bitmap);
    }
    public Rgb ReadDominant()
    {
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        var bits = bitmap.LockBits(new Rectangle(0, 0, bounds.Width, bounds.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[Math.Abs(bits.Stride) * bounds.Height]; Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length);
            return AmbientEffect.Dominant(pixels, bounds.Width, bounds.Height, bits.Stride);
        }
        finally { bitmap.UnlockBits(bits); }
    }
    public void Dispose() { graphics.Dispose(); bitmap.Dispose(); }
}
