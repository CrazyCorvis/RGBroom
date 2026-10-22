using System;
using System.Linq;
using System.Numerics;

namespace MagicHomeNET;
public static class SpectrumAnalyzer
{
    public const int Size = 2048;
    public static double[] Analyze(float[][] channels, int rate)
    {
        var power = new double[Size / 2 + 1];
        if (channels.Length == 0 || rate <= 0) return new double[3];
        double windowSum = 0;
        var window = new double[Size];
        for (int i = 0; i < Size; i++) { window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / (Size - 1)); windowSum += window[i] * window[i]; }
        foreach (var samples in channels)
        {
            if (samples.Length != Size) throw new ArgumentException("FFT requires 2048 samples per channel.");
            double mean = samples.Average(x => (double)x);
            var data = new Complex[Size];
            for (int i = 0; i < Size; i++) data[i] = new((samples[i] - mean) * window[i], 0);
            Fft(data);
            for (int i = 0; i < power.Length; i++) power[i] += data[i].Magnitude * data[i].Magnitude / channels.Length;
        }
        double[] bands = new double[3];
        for (int bin = 1; bin < power.Length; bin++)
        {
            double f = bin * rate / (double)Size;
            int band = f < 20 || f >= 20000 ? -1 : f < 250 ? 0 : f < 2000 ? 1 : 2;
            if (band >= 0) bands[band] += power[bin] * (bin == Size / 2 ? 1 : 2) / (Size * windowSum);
        }
        return bands.Select(Math.Sqrt).ToArray();
    }
    static void Fft(Complex[] a)
    {
        for (int i = 1, j = 0; i < a.Length; i++)
        {
            int bit = a.Length >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int size = 2; size <= a.Length; size <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / size);
            for (int start = 0; start < a.Length; start += size)
            {
                Complex factor = 1;
                for (int i = 0; i < size / 2; i++)
                {
                    var even = a[start + i]; var odd = a[start + i + size / 2] * factor;
                    a[start + i] = even + odd; a[start + i + size / 2] = even - odd; factor *= step;
                }
            }
        }
    }
}
public class MusicEffect
{
    double envelope, hue, clock, flashUntil, baseline, previousEnergy, lastBeat = -10;
    int flashIndex = -1;
    readonly double[] spectral = new double[3];
    (MusicStyle, bool)? previous;
    Rgb flash;
    static readonly Rgb[] Palette = { new(255,0,0), new(0,255,255), new(255,0,255), new(0,255,0), new(0,0,255), new(255,180,0) };
    public Rgb Process(double[] levels, EngineOptions settings, double dt)
    {
        bool spectrum = settings.Mode == LightMode.Spectrum;
        var key = (settings.Style, spectrum);
        if (previous != key) { envelope = flashUntil = baseline = previousEnergy = 0; lastBeat = -10; flashIndex = -1; Array.Clear(spectral); previous = key; }
        dt = Math.Clamp(dt, .001, .25); clock += dt;
        var weighted = levels.Select((v, i) => Math.Max(0, v - .001) * settings.Weights[i]).ToArray();
        double energy = Math.Sqrt(weighted.Sum(v => v * v)) * settings.Gain, target = Math.Min(1, energy);
        if (settings.Style == MusicStyle.Strobe)
        {
            double duration = settings.FlashMilliseconds / 1000;
            bool rise = energy - previousEnergy > Math.Max(.015, previousEnergy * .08);
            if (energy > .08 && rise && energy > baseline * 1.12 && clock - lastBeat >= Math.Max(.075, duration + .02))
            {
                lastBeat = clock; flashUntil = clock + duration;
                flashIndex = (flashIndex + 1) % Palette.Length;
                flash = spectrum ? SpectrumColor(weighted, 1) : Palette[flashIndex];
            }
            baseline += (energy - baseline) * (1 - Math.Exp(-dt / .18)); previousEnergy = energy;
            return clock < flashUntil ? flash : Rgb.Black;
        }
        var (attack, release, hueSpeed) = settings.Style switch { MusicStyle.Pulse => (.06, .28, .08), MusicStyle.Energetic => (.012, .08, .3), _ => (.38, .65, .025) };
        envelope += (target - envelope) * (1 - Math.Exp(-dt / (target > envelope ? attack : release)));
        hue = (hue + dt * hueSpeed) % 1;
        double value = settings.Style == MusicStyle.Energetic ? Math.Pow(envelope, .65) : envelope;
        if (target == 0 && envelope < .003) value = 0;
        if (spectrum)
        {
            for (int i = 0; i < 3; i++) spectral[i] += (weighted[i] - spectral[i]) * (1 - Math.Exp(-dt / (weighted[i] > spectral[i] ? attack : release)));
            return SpectrumColor(spectral, value);
        }
        int dominant = Array.IndexOf(weighted, weighted.Max());
        return Rgb.Hsv(hue + (settings.Style == MusicStyle.Energetic ? dominant / 3d : 0), 1, value);
    }
    static Rgb SpectrumColor(double[] bands, double value)
    {
        double peak = bands.Max(); if (peak <= 0) return Rgb.Black;
        return new(Rgb.Clamp(bands[0] / peak * value * 255), Rgb.Clamp(bands[2] / peak * value * 255), Rgb.Clamp(bands[1] / peak * value * 255));
    }
}
public class AmbientEffect
{
    double[]? color;
    public Rgb Process(Rgb target, double dt)
    {
        var next = new double[] { target.R, target.G, target.B };
        if (color is null) color = next;
        else for (int i = 0; i < 3; i++) color[i] += (next[i] - color[i]) * (1 - Math.Exp(-Math.Max(.001, dt) / .22));
        return new(Rgb.Clamp(color[0]), Rgb.Clamp(color[1]), Rgb.Clamp(color[2]));
    }
    public static Rgb Dominant(byte[] bgra, int width, int height, int stride)
    {
        var counts = new int[512]; var red = new long[512]; var green = new long[512]; var blue = new long[512];
        for (int y = 0; y < height; y += Math.Max(1, height / 120))
            for (int x = 0; x < width; x += Math.Max(1, width / 160))
            {
                int p = (stride >= 0 ? y * stride : (height - y - 1) * -stride) + x * 4;
                int r = bgra[p + 2], g = bgra[p + 1], b = bgra[p], k = (r / 32) * 64 + (g / 32) * 8 + b / 32;
                counts[k]++; red[k] += r; green[k] += g; blue[k] += b;
            }
        int winner = Array.IndexOf(counts, counts.Max());
        if (counts[winner] == 0) return Rgb.Black;
        return new(Rgb.Clamp(red[winner] / (double)counts[winner]), Rgb.Clamp(green[winner] / (double)counts[winner]), Rgb.Clamp(blue[winner] / (double)counts[winner]));
    }
}
