using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace MagicHomeNET;
public sealed class PcLightingWindow : Window
{
    readonly TextBox host = new() { Text = "127.0.0.1", MinWidth = 180 };
    readonly TextBox port = new() { Text = "6742", Width = 75 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 10) };
    readonly StackPanel rows = new();
    readonly Button scan = new() { Content = "Найти компоненты", Margin = new(12, 0, 0, 0) };
    readonly List<(CheckBox Box, PcController Device, int Zone)> choices = new();
    readonly CancellationTokenSource stop = new();
    string scannedHost = ""; int scannedPort;
    public DeviceEntry[] Added { get; private set; } = Array.Empty<DeviceEntry>();
    public PcLightingWindow()
    {
        Title = "Подсветка ПК · OpenRGB"; Width = 730; Height = 650; MinWidth = 640; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        var root = new DockPanel { Margin = new(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Подсветка компонентов ПК", FontSize = 22, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "Asus Aura Sync · MSI Mystic Light · Gigabyte RGB Fusion · ASRock Polychrome\nНужен OpenRGB с включённым SDK-сервером. Поддержка зависит от модели устройства. Закройте другие программы управления RGB.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 14) });
        var server = new WrapPanel { VerticalAlignment = VerticalAlignment.Center }; top.Children.Add(server);
        server.Children.Add(new TextBlock { Text = "Сервер ", VerticalAlignment = VerticalAlignment.Center }); server.Children.Add(host);
        server.Children.Add(new TextBlock { Text = " Порт ", VerticalAlignment = VerticalAlignment.Center }); server.Children.Add(port); server.Children.Add(scan);
        top.Children.Add(status);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(new TextBlock { Text = "Выберите весь компонент или отдельные зоны. Размер ARGB-зон задаётся в OpenRGB. «Выключить» отправляет чёрный цвет; режимы в память устройства не записываются.", TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new(0, 10, 0, 10) });
        var add = new Button { Content = "Добавить выбранные", HorizontalAlignment = HorizontalAlignment.Right }; add.Click += Add; bottom.Children.Add(add);
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        scan.Click += Scan;
        Closed += (_, _) => stop.Cancel();
    }
    async void Scan(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(host.Text) || !int.TryParse(port.Text, out int p) || p is < 1 or > 65535) { status.Text = "Укажите сервер и порт от 1 до 65535."; return; }
        scan.IsEnabled = false; choices.Clear(); rows.Children.Clear(); status.Text = "Запрос устройств OpenRGB…";
        scannedHost = host.Text.Trim(); scannedPort = p;
        try
        {
            var devices = await OpenRgbConnection.Discover(scannedHost, p, stop.Token);
            foreach (var d in devices)
            {
                bool available = d.Direct && devices.Count(x => x.Identity == d.Identity) == 1;
                var whole = new CheckBox { Content = $"{d.Vendor} {d.Name} · {d.Colors} LED" + (available ? "" : " · недоступен Direct или неоднозначный ID"), IsEnabled = available && d.Colors > 0, Margin = new(0, 8, 0, 6) };
                choices.Add((whole, d, -1)); rows.Children.Add(whole);
                for (int i = 0; i < d.Zones.Length; i++)
                {
                    var z = d.Zones[i]; var box = new CheckBox { Content = $"{z.Name} · {z.Leds} LED", IsEnabled = available && z.Leds > 0, Margin = new(24, 3, 0, 3) };
                    choices.Add((box, d, i)); rows.Children.Add(box);
                }
            }
            status.Text = devices.Length == 0 ? "OpenRGB не обнаружил компоненты. Проверьте поддержку оборудования и настройку зон." : $"Компонентов: {devices.Length}. Отметьте нужные компоненты или зоны.";
        }
        catch (OperationCanceledException) { if (!stop.IsCancellationRequested) status.Text = "OpenRGB не ответил вовремя."; }
        catch (Exception ex) { status.Text = "Нет связи с OpenRGB: " + ex.Message + " Запустите OpenRGB → SDK Server → Start Server (порт 6742)."; }
        finally { scan.IsEnabled = true; }
    }
    void Add(object sender, RoutedEventArgs e)
    {
        var selected = choices.Where(x => x.Box.IsChecked == true && x.Box.IsEnabled).ToArray();
        if (selected.Length == 0) { status.Text = "Выберите хотя бы один компонент или зону."; return; }
        if (selected.GroupBy(x => x.Device.Identity).Any(g => g.Count() > 1 && g.Any(x => x.Zone < 0))) { status.Text = "Выберите весь компонент либо его отдельные зоны, чтобы они не управлялись дважды."; return; }
        Added = selected.Select(x => new DeviceEntry { Protocol = ProtocolKind.OpenRgb,
            Ip = OpenRgbConnection.Address(scannedHost, scannedPort, x.Device, x.Zone),
            Name = x.Device.Name + (x.Zone < 0 ? " · весь компонент" : " · " + x.Device.Zones[x.Zone].Name),
            OpenRgbZoneName = x.Zone < 0 ? "" : x.Device.Zones[x.Zone].Name }).ToArray();
        DialogResult = true;
    }
}
