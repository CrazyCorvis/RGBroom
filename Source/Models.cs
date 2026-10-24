using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MagicHomeNET;
public enum LightMode { Color, Transition, Music, Spectrum, Ambient }
public enum MusicStyle { Smooth, Pulse, Energetic, Strobe }
public enum ProtocolKind { Auto, Rgb8, Rgb9, Legacy, LegacyRgbw, Spi2, Spi3, Hsv25, Wled, Yeelight, ElkBledom, Triones, Tuya }
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Black => new(0, 0, 0);
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    public static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    public Rgb Scale(double amount) => new(Clamp(R * amount), Clamp(G * amount), Clamp(B * amount));
    public static Rgb Hsv(double hue, double saturation = 1, double value = 1)
    {
        hue = (hue % 1 + 1) % 1;
        double c = value * saturation, x = c * (1 - Math.Abs(hue * 6 % 2 - 1)), m = value - c;
        var (r, g, b) = (int)(hue * 6) switch { 0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x) };
        return new(Clamp((r + m) * 255), Clamp((g + m) * 255), Clamp((b + m) * 255));
    }
}
public class DeviceEntry : INotifyPropertyChanged
{
    string ip = "", name = "", status = "Не подключён";
    bool selected = true;
    ProtocolKind protocol;
    public string Ip { get => ip; set => Set(ref ip, value); }
    public string Name { get => name; set => Set(ref name, value); }
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public ProtocolKind Protocol { get => protocol; set => Set(ref protocol, value); }
    public string DeviceId { get; set; } = "";
    public string ProtectedKey { get; set; } = "";
    public string TuyaVersion { get; set; } = "3.3";
    public bool TuyaDevice22 { get; set; }
    public int PowerDp { get; set; }
    public int ModeDp { get; set; }
    public int ColorDp { get; set; }
    public bool BluetoothRandom { get; set; }
    public bool BluetoothElk13 { get; set; }
    public static bool IsBle(ProtocolKind p) => p is ProtocolKind.ElkBledom or ProtocolKind.Triones;
    public static bool ValidAddress(string value, ProtocolKind p) => IsBle(p) ? BluetoothConnection.IsValidAddress(value) : System.Net.IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Status { get => status; set => Set(ref status, value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { field = value; PropertyChanged?.Invoke(this, new(name)); }
}
public class AudioProfile
{
    public double Brightness { get; set; } = 65;
    public double Gain { get; set; } = 8;
    public double Low { get; set; } = 100;
    public double Mid { get; set; } = 100;
    public double High { get; set; } = 100;
    public double FlashBrightness { get; set; } = 100;
    public double FlashMilliseconds { get; set; } = 90;
    public MusicStyle Style { get; set; }
    public string OutputId { get; set; } = "";
    public string OutputName { get; set; } = "";
    public void Validate()
    {
        Brightness = Safe(Brightness, 0, 100, 65); Gain = Safe(Gain, 1, 30, 8);
        Low = Safe(Low, 0, 200, 100); Mid = Safe(Mid, 0, 200, 100); High = Safe(High, 0, 200, 100);
        FlashBrightness = Safe(FlashBrightness, 0, 100, 100); FlashMilliseconds = Safe(FlashMilliseconds, 30, 160, 90);
        if (!Enum.IsDefined(Style)) Style = MusicStyle.Smooth;
    }
    public static double Safe(double v, double min, double max, double fallback) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}
public class UserSettings
{
    public int Schema { get; set; } = 1;
    public List<DeviceEntry> Devices { get; set; } = new();
    public Rgb Color { get; set; } = new(70, 100, 255);
    public double Brightness { get; set; } = 65;
    public double TransitionBrightness { get; set; } = 65;
    public double TransitionSeconds { get; set; } = 12;
    public AudioProfile Music { get; set; } = new();
    public AudioProfile Spectrum { get; set; } = new();
    public double AmbientBrightness { get; set; } = 65;
    public string Display { get; set; } = "";
    public void Validate()
    {
        Brightness = AudioProfile.Safe(Brightness, 0, 100, 65); TransitionBrightness = AudioProfile.Safe(TransitionBrightness, 0, 100, 65);
        AmbientBrightness = AudioProfile.Safe(AmbientBrightness, 0, 100, 65); TransitionSeconds = AudioProfile.Safe(TransitionSeconds, 3, 60, 12);
        Music ??= new(); Spectrum ??= new(); Music.Validate(); Spectrum.Validate();
        Devices = (Devices ?? new()).Where(d => d != null && DeviceEntry.ValidAddress(d.Ip, d.Protocol)).DistinctBy(d => d.Ip).ToList();
    }
}
public record EngineOptions(LightMode Mode, Rgb Color, double Brightness, double Period, MusicStyle Style,
    double Gain, double[] Weights, double FlashBrightness, double FlashMilliseconds, string OutputId, string OutputName, string Display);
public static class SettingsStore
{
    public static string DirectoryPath => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "RGBroom")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RGBroom");
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static UserSettings Load()
    {
        try
        {
            UserSettings settings;
            if (File.Exists(FilePath)) settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new();
            else if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagicHomeNET", "settings.json")))
                settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagicHomeNET", "settings.json"))) ?? new();
            else
            {
                var oldPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagicHomePC", "settings.json");
                settings = File.Exists(oldPath) ? ImportLegacy(File.ReadAllText(oldPath)) : new();
            }
            settings.Validate(); return settings;
        }
        catch (Exception ex) { App.Log(ex); return new(); }
    }
    public static void Save(UserSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));
        File.Move(temporary, FilePath, true);
    }
    public static UserSettings ImportLegacy(string json)
    {
        using var document = JsonDocument.Parse(json);
        var r = document.RootElement;
        double Number(JsonElement e, string n, double fallback) => e.TryGetProperty(n, out var v) && v.TryGetDouble(out var d) ? d : fallback;
        string Text(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var s = new UserSettings { Brightness = Number(r, "brightness", 65), TransitionBrightness = Number(r, "transition_brightness", 65), TransitionSeconds = Number(r, "speed", 12), AmbientBrightness = Number(r, "ambient_brightness", 65), Display = Text(r, "display") };
        if (s.Display == "Основной дисплей") s.Display = "";
        if (r.TryGetProperty("rgb", out var rgb) && rgb.GetArrayLength() == 3) s.Color = new(Rgb.Clamp(rgb[0].GetDouble()), Rgb.Clamp(rgb[1].GetDouble()), Rgb.Clamp(rgb[2].GetDouble()));
        if (r.TryGetProperty("devices", out var devices))
            foreach (var d in devices.EnumerateArray()) s.Devices.Add(new() { Ip = Text(d, "ip"), Name = Text(d, "name"), Selected = !d.TryGetProperty("checked", out var ch) || ch.GetBoolean() });
        else if (Text(r, "ip") is { Length: > 0 } ip) s.Devices.Add(new() { Ip = ip });
        AudioProfile Profile(string name)
        {
            var p = r.TryGetProperty(name, out var saved) ? saved : r;
            var a = new AudioProfile { Brightness = Number(p, "brightness", s.Brightness), Gain = Number(p, "gain", 8), FlashBrightness = Number(p, "flash_brightness", 100), FlashMilliseconds = Number(p, "flash_ms", 90), OutputName = Text(p, "output") };
            if (a.OutputName.Length == 0) a.OutputName = Text(r, "audio_output");
            if (a.OutputName == "Основной выход Windows") a.OutputName = "";
            string style = Text(p, "style"); if (style.Length == 0) style = Text(r, "music_style");
            a.Style = style switch { "Пульсация" => MusicStyle.Pulse, "Энергичный" => MusicStyle.Energetic, "Стробоскоп" => MusicStyle.Strobe, _ => MusicStyle.Smooth };
            if (p.TryGetProperty("eq", out var eq) && eq.GetArrayLength() == 3) { a.Low = eq[0].GetDouble(); a.Mid = eq[1].GetDouble(); a.High = eq[2].GetDouble(); }
            return a;
        }
        s.Music = Profile("music"); s.Spectrum = Profile("spectrum"); s.Validate(); return s;
    }
}
