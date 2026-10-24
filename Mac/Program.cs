using System.Text.Json;

namespace MagicHomeNET;

public static class App
{
    static readonly object outputLock = new();
    static readonly JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true };
    static void Emit(object value) { lock (outputLock) Console.WriteLine(JsonSerializer.Serialize(value)); }
    public static void Log(Exception ex)
    {
        if (SelfTests.Active) return;
        try { Directory.CreateDirectory(SettingsStore.DirectoryPath); File.AppendAllText(Path.Combine(SettingsStore.DirectoryPath, "errors.log"), DateTimeOffset.Now + " " + ex + Environment.NewLine); } catch { }
    }
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            try { await SelfTests.Run(); return 0; }
            catch (Exception ex) { SelfTests.Report("FAIL: " + ex); Console.Error.WriteLine(ex); return 1; }
        }
        var settings = SettingsStore.Load();
        await using var group = new LightGroup();
        await using var engine = new LightEngine(group, (color, levels) => Emit(new { type = "frame", color, levels }), message => Emit(new { type = "error", message }));
        var mode = LightMode.Color;
        void Apply()
        {
            var p = mode == LightMode.Spectrum ? settings.Spectrum : settings.Music;
            double brightness = mode switch { LightMode.Transition => settings.TransitionBrightness, LightMode.Ambient => settings.AmbientBrightness, LightMode.Music or LightMode.Spectrum => p.Brightness, _ => settings.Brightness };
            engine.Update(new(mode, settings.Color, brightness, settings.TransitionSeconds, p.Style, p.Gain,
                new[] { p.Low / 100, p.Mid / 100, p.High / 100 }, p.FlashBrightness, p.FlashMilliseconds, "", "", settings.Display));
        }
        Emit(new { type = "settings", settings });
        Apply();
        while (await Console.In.ReadLineAsync() is { } line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                switch (r.GetProperty("op").GetString())
                {
                    case "apply":
                        var updated = r.GetProperty("settings").Deserialize<UserSettings>(json) ?? throw new IOException("Некорректные настройки");
                        // The engine owns secrets. A queued UI snapshot must not overwrite a newly saved key.
                        foreach (var d in updated.Devices)
                            if (settings.Devices.FirstOrDefault(old => old.Ip == d.Ip) is { } existing)
                                d.ProtectedKey = existing.ProtectedKey;
                        settings = updated;
                        settings.Validate();
                        mode = (LightMode)r.GetProperty("mode").GetInt32();
                        if (!Enum.IsDefined(mode)) throw new IOException("Неизвестный режим");
                        SettingsStore.Save(settings); Apply(); break;
                    case "connect":
                        await group.ConnectAsync(settings.Devices.Where(d => d.Selected), (id, status) => Emit(new { type = "status", id, status }));
                        Apply(); break;
                    case "disconnect": await group.DisposeAsync(); break;
                    case "power": engine.Power(r.GetProperty("on").GetBoolean()); break;
                    case "retry": engine.Restart(); break;
                    case "scan":
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                        {
                            IEnumerable<DeviceEntry> found;
                            if (r.GetProperty("ble").GetBoolean()) found = await BluetoothConnection.ScanAsync(timeout.Token);
                            else found = (await DeviceDiscovery.ScanAsync(timeout.Token)).Concat(await ExtraWifiDiscovery.ScanAsync(timeout.Token));
                            Emit(new { type = "discovered", devices = found });
                        }
                        break;
                    case "displays": Emit(new { type = "displays", displays = ScreenSource.Displays() }); break;
                    case "secret":
                        var device = settings.Devices.First(d => d.Ip == r.GetProperty("id").GetString());
                        string key = r.GetProperty("key").GetString() ?? "";
                        if (System.Text.Encoding.UTF8.GetByteCount(key) != 16) throw new IOException("local_key должен содержать 16 байт UTF-8.");
                        string old = device.ProtectedKey;
                        device.ProtectedKey = LocalSecrets.Protect(key);
                        SettingsStore.Save(settings);
                        LocalSecrets.Delete(old);
                        Emit(new { type = "settings", settings }); break;
                    case "quit": return 0;
                }
                Emit(new { type = "done", op = r.GetProperty("op").GetString() });
            }
            catch (Exception ex) { Log(ex); Emit(new { type = "error", message = ex.Message }); }
        }
        return 0;
    }
}
