using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MagicHomeNET;
public sealed class LightEngine : IAsyncDisposable
{
    readonly LightGroup group;
    readonly CancellationTokenSource stop = new();
    readonly Task task;
    readonly Action<Rgb, double[]> feedback;
    readonly Action<string> status;
    readonly Func<string, string, IAudioSource> audioFactory;
    readonly Func<string, IScreenSource> screenFactory;
    EngineOptions? options;
    int revision;
    bool paused;
    public LightEngine(LightGroup group, Action<Rgb, double[]> feedback, Action<string> status,
        Func<string, string, IAudioSource>? audioFactory = null, Func<string, IScreenSource>? screenFactory = null)
    {
        this.group = group; this.feedback = feedback; this.status = status;
        this.audioFactory = audioFactory ?? ((id, name) => new AudioSource(id, name));
        this.screenFactory = screenFactory ?? (id => new ScreenSource(id));
        task = Task.Run(Run);
    }
    public void Update(EngineOptions value) { Volatile.Write(ref options, value); Interlocked.Increment(ref revision); }
    public void Restart() => Interlocked.Increment(ref revision);
    public void Power(bool on) { Volatile.Write(ref paused, !on); group.Power(on); Interlocked.Increment(ref revision); }
    async Task Run()
    {
        IAudioSource? audio = null; IScreenSource? screen = null;
        EngineOptions? previous = null; int applied = -1, failed = -1;
        var music = new MusicEffect(); var ambient = new AmbientEffect();
        var clock = Stopwatch.StartNew(); double lastTime = clock.Elapsed.TotalSeconds, lastFeedback = -1;
        Rgb? last = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var current = Volatile.Read(ref options); int version = Volatile.Read(ref revision);
                if (current == null || group.Count == 0 || Volatile.Read(ref paused) || version == failed)
                {
                    audio?.Dispose(); audio = null; screen?.Dispose(); screen = null; previous = null; last = null;
                    await Task.Delay(35, stop.Token); continue;
                }
                try
                {
                    bool changed = previous == null || previous.Mode != current.Mode || previous.OutputId != current.OutputId || previous.OutputName != current.OutputName || previous.Display != current.Display;
                    if (changed)
                    {
                        audio?.Dispose(); audio = null; screen?.Dispose(); screen = null;
                        if (current.Mode is LightMode.Music or LightMode.Spectrum) audio = audioFactory(current.OutputId, current.OutputName);
                        if (current.Mode == LightMode.Ambient) screen = screenFactory(current.Display);
                        music = new(); ambient = new(); last = null; lastTime = clock.Elapsed.TotalSeconds;
                    }
                    if (version != applied) { last = null; applied = version; }
                    previous = current;
                    double now = clock.Elapsed.TotalSeconds, dt = now - lastTime; lastTime = now;
                    double[] levels = new double[3];
                    Rgb color = current.Color;
                    if (current.Mode == LightMode.Transition) color = Rgb.Hsv(now / current.Period);
                    if (current.Mode is LightMode.Music or LightMode.Spectrum)
                    {
                        levels = SpectrumAnalyzer.Analyze(audio!.Snapshot(), audio.SampleRate);
                        color = music.Process(levels, current, dt);
                    }
                    if (current.Mode == LightMode.Ambient) color = ambient.Process(screen!.ReadDominant(), dt);
                    double brightness = (current.Mode is LightMode.Music or LightMode.Spectrum) && current.Style == MusicStyle.Strobe ? current.FlashBrightness : current.Brightness;
                    var output = color.Scale(brightness / 100);
                    if (last != output) { group.Publish(output, current.Mode == LightMode.Color); last = output; }
                    if (now - lastFeedback >= .08) { feedback(output, levels.Select(v => Math.Min(1, v * current.Gain)).ToArray()); lastFeedback = now; }
                    await Task.Delay(current.Mode == LightMode.Ambient ? 80 : current.Mode is LightMode.Music or LightMode.Spectrum ? 20 : 60, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex) { failed = version; previous = null; audio?.Dispose(); audio = null; screen?.Dispose(); screen = null; status("Ошибка: " + ex.Message); App.Log(ex); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { audio?.Dispose(); screen?.Dispose(); }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); try { await task.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } stop.Dispose(); }
}
