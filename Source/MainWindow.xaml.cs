using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MagicHomeNET;
public partial class MainWindow : Window
{
    readonly UserSettings settings;
    readonly bool testing;
    readonly ObservableCollection<DeviceEntry> devices;
    readonly LightGroup group = new();
    readonly LightEngine engine;
    readonly CancellationTokenSource lifetime = new();
    LightMode mode;
    bool ready, closing, connecting;
    int connectionGeneration;
    IReadOnlyList<AudioEndpoint> audioDevices = new[] { new AudioEndpoint("", "Основной выход Windows") };
    IReadOnlyList<DisplayEndpoint> displays = new[] { new DisplayEndpoint("", "Основной дисплей") };
    double[] levels = new double[3];
    readonly List<ProgressBar> meters = new();
    public MainWindow(bool testing = false)
    {
        this.testing = testing;
        settings = testing ? new UserSettings() : SettingsStore.Load();
        InitializeComponent();
        devices = new(settings.Devices); DeviceGrid.ItemsSource = devices;
        var protocols=Enum.GetValues<ProtocolKind>().Select(p=>new ProtocolChoice(p,ProtocolName(p))).ToArray();
        ProtocolColumn.ItemsSource = protocols;
        AddProtocol.ItemsSource=protocols;AddProtocol.SelectedIndex=0;
        DevicePanel.IsExpanded = devices.Count == 0;
        engine = new(group, (color, bands) => OnUi(() => Feedback(color, bands)), text => OnUi(() => Status(text)));
        ready = true;
        Navigation.SelectedIndex = 0;
        Closing += CloseWindow;
        Loaded += async (_, _) => { if (!testing) await RefreshSources(false); };
    }
    void OnUi(Action action) { if (!closing && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { if (!closing) action(); }); }
    void Status(string text) => StatusText.Text = text;
    public record ProtocolChoice(ProtocolKind Key,string Name){public override string ToString()=>Name;}
    public static string ProtocolName(ProtocolKind p)=>p switch
    {
        ProtocolKind.OpenRgb=>"OpenRGB · подсветка ПК",ProtocolKind.Auto=>"Magic Home · авто",ProtocolKind.Wled=>"WLED · Wi-Fi",ProtocolKind.Yeelight=>"Yeelight LAN",ProtocolKind.Tuya=>"Tuya / Smart Life",
        ProtocolKind.ElkBledom=>"Lotus / ELK-BLEDOM",ProtocolKind.Triones=>"Triones / LED BLE",_=>"Magic Home · "+p
    };
    public EngineOptions Snapshot()
    {
        var p = mode == LightMode.Spectrum ? settings.Spectrum : settings.Music;
        double brightness = mode switch { LightMode.Color => settings.Brightness, LightMode.Transition => settings.TransitionBrightness, LightMode.Ambient => settings.AmbientBrightness, _ => p.Brightness };
        return new(mode, settings.Color, brightness, settings.TransitionSeconds, p.Style, p.Gain, new[] { p.Low / 100, p.Mid / 100, p.High / 100 }, p.FlashBrightness, p.FlashMilliseconds, p.OutputId, p.OutputName, settings.Display);
    }
    void Apply() { if (ready && !closing) engine.Update(Snapshot()); }
    void Feedback(Rgb color, double[] bands)
    {
        ColorPreview.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)); HexText.Text = color.Hex; levels = bands;
        for (int i = 0; i < meters.Count; i++) meters[i].Value = bands[i] * 100;
    }
    void Navigate(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || Navigation.SelectedItem is not ListBoxItem item) return;
        mode = Enum.Parse<LightMode>(item.Tag.ToString()!);
        BuildPage(); Apply();
    }
    Border Card(StackPanel content)
    {
        var border = new Border { Style = (Style)FindResource("Card"), Child = content };
        PageContent.Children.Add(border); return border;
    }
    static TextBlock Label(string text, double size = 14) => new() { Text = text, FontSize = size, Margin = new Thickness(0, 0, 0, 8) };
    static StackPanel Row() => new() { Orientation = Orientation.Horizontal };
    Button Button(string label, Action action)
    {
        var button = new Button { Content = label }; button.Click += (_, _) => action(); return button;
    }
    void Slider(StackPanel parent, string text, double value, double minimum, double maximum, Action<double> changed, string unit = "%")
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 12) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(70) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var label = Label(text); grid.Children.Add(label);
        var number = new TextBlock { Text = $"{value:0} {unit}", HorizontalAlignment = HorizontalAlignment.Right, Opacity = .7 };
        Grid.SetColumn(number, 1); grid.Children.Add(number);
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10 };
        System.Windows.Automation.AutomationProperties.SetName(slider, text);
        Grid.SetRow(slider, 1); Grid.SetColumnSpan(slider, 2); grid.Children.Add(slider);
        slider.ValueChanged += (_, _) => { changed(slider.Value); number.Text = $"{slider.Value:0} {unit}"; Apply(); };
        parent.Children.Add(grid);
    }
    void BuildPage()
    {
        PageContent.Children.Clear(); meters.Clear();
        PageTitle.Text = mode switch { LightMode.Color => "Цвет", LightMode.Transition => "Переход", LightMode.Music => "Цветомузыка", LightMode.Spectrum => "Спектр", _ => "Фон" };
        PageDescription.Text = mode switch
        {
            LightMode.Color => "Постоянное свечение выбранным цветом.",
            LightMode.Transition => "Плавная смена цветов по всему спектру.",
            LightMode.Music => "Свет реагирует на музыку с выбранного выхода звука.",
            LightMode.Spectrum => "Низкие — красный, средние — синий, высокие — зелёный.",
            _ => "Лента повторяет преобладающий цвет выбранного дисплея."
        };
        if (mode == LightMode.Color)
        {
            var content = new StackPanel(); Card(content); content.Children.Add(Label("Цвет свечения", 16));
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
            foreach (string hex in new[] { "#FF3030", "#FFB020", "#20FF70", "#20CFFF", "#8040FF", "#FFFFFF" })
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var swatch = new Button { Width = 44, Height = 36, Background = new SolidColorBrush(color), ToolTip = hex, Margin = new Thickness(0, 0, 8, 8) };
                System.Windows.Automation.AutomationProperties.SetName(swatch, "Цвет " + hex);
                swatch.Click += (_, _) => { settings.Color = new(color.R, color.G, color.B); BuildPage(); Apply(); }; row.Children.Add(swatch);
            }
            content.Children.Add(row);
            content.Children.Add(Button("Выбрать другой цвет…", ChooseColor));
            var rgb = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            Slider(rgb, "Красный", settings.Color.R, 0, 255, v => settings.Color = settings.Color with { R = (byte)v }, "");
            Slider(rgb, "Зелёный", settings.Color.G, 0, 255, v => settings.Color = settings.Color with { G = (byte)v }, "");
            Slider(rgb, "Синий", settings.Color.B, 0, 255, v => settings.Color = settings.Color with { B = (byte)v }, "");
            var expander = new Expander { Header = "Точная настройка RGB", Content = rgb, Margin = new Thickness(0, 12, 0, 0) }; content.Children.Add(expander);
            var brightness = new StackPanel(); Card(brightness); Slider(brightness, "Яркость", settings.Brightness, 0, 100, v => settings.Brightness = v);
        }
        else if (mode == LightMode.Transition)
        {
            var content = new StackPanel(); Card(content);
            Slider(content, "Яркость", settings.TransitionBrightness, 0, 100, v => settings.TransitionBrightness = v);
            Slider(content, "Длительность полного круга", settings.TransitionSeconds, 3, 60, v => settings.TransitionSeconds = v, "с");
        }
        else if (mode is LightMode.Music or LightMode.Spectrum) BuildMusic(mode == LightMode.Spectrum ? settings.Spectrum : settings.Music);
        else
        {
            var content = new StackPanel(); Card(content); content.Children.Add(Label("Дисплей"));
            var source = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
            var update = Button("Обновить", async () => await RefreshSources()); DockPanel.SetDock(update, Dock.Right); source.Children.Add(update);
            var choices = displays.ToList();
            if (!choices.Any(d => d.Id == settings.Display)) choices.Add(new(settings.Display, "Сохранённый дисплей · выберите доступный"));
            var combo = new ComboBox { ItemsSource = choices, SelectedItem = choices.First(d => d.Id == settings.Display), Margin = new Thickness(0, 0, 10, 0) };
            System.Windows.Automation.AutomationProperties.SetName(combo, "Дисплей для фонового освещения");
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is DisplayEndpoint d) { settings.Display = d.Id; Apply(); } };
            source.Children.Add(combo); content.Children.Add(source);
            Slider(content, "Яркость", settings.AmbientBrightness, 0, 100, v => settings.AmbientBrightness = v);
            content.Children.Add(new TextBlock { Text = "Цвет меняется с лёгким сглаживанием. Изображение обрабатывается в памяти и не сохраняется.", Opacity = .65, FontSize = 12 });
        }
    }
    void BuildMusic(AudioProfile p)
    {
        var source = new StackPanel(); Card(source); source.Children.Add(Label("Выход звука"));
        var dock = new DockPanel();
        var refresh = Button("Обновить", async () => await RefreshSources()); DockPanel.SetDock(refresh, Dock.Right); dock.Children.Add(refresh);
        var choices = audioDevices.ToList();
        AudioEndpoint selected;
        if (p.OutputId.Length == 0 && p.OutputName.Length > 0)
        {
            string oldName = p.OutputName.Replace(" [Loopback]", "", StringComparison.OrdinalIgnoreCase);
            var match = choices.FirstOrDefault(d => d.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase) || d.Name.StartsWith(oldName, StringComparison.OrdinalIgnoreCase));
            if (match != null) { p.OutputId = match.Id; p.OutputName = ""; }
        }
        var existing = choices.FirstOrDefault(d => d.Id == p.OutputId && p.OutputName.Length == 0);
        if (existing != null) selected = existing;
        else { selected = new(p.OutputId, p.OutputName.Length > 0 ? p.OutputName : "Сохранённый выход недоступен"); choices.Add(selected); }
        var output = new ComboBox { ItemsSource = choices, SelectedItem = selected, Margin = new Thickness(0, 0, 10, 0) };
        System.Windows.Automation.AutomationProperties.SetName(output, "Выход звука для цветомузыки");
        output.SelectionChanged += (_, _) => { if (output.SelectedItem is AudioEndpoint d) { p.OutputId = d.Id; p.OutputName = ""; Apply(); } };
        dock.Children.Add(output); source.Children.Add(dock);
        var reaction = new StackPanel(); Card(reaction); reaction.Children.Add(Label("Реакция"));
        var style = new ComboBox { ItemsSource = new[] { "Плавный", "Пульсация", "Энергичный", "Стробоскоп" }, SelectedIndex = (int)p.Style, Margin = new Thickness(0, 0, 0, 16) };
        System.Windows.Automation.AutomationProperties.SetName(style, "Вариант реакции на музыку");
        style.SelectionChanged += (_, _) => { if (style.SelectedIndex >= 0) { p.Style = (MusicStyle)style.SelectedIndex; BuildPage(); Apply(); } }; reaction.Children.Add(style);
        if (p.Style == MusicStyle.Strobe)
        {
            Slider(reaction, "Яркость вспышек", p.FlashBrightness, 0, 100, v => p.FlashBrightness = v);
            Slider(reaction, "Длительность вспышки", p.FlashMilliseconds, 30, 160, v => p.FlashMilliseconds = v, "мс");
        }
        else Slider(reaction, "Яркость", p.Brightness, 0, 100, v => p.Brightness = v);
        Slider(reaction, "Общая чувствительность", p.Gain, 1, 30, v => p.Gain = v, "");
        var eq = new StackPanel(); Card(eq); eq.Children.Add(Label("Частотная чувствительность", 16));
        eq.Children.Add(new TextBlock { Text = "0% отключает полосу · 100% — обычная чувствительность", Opacity = .65, FontSize = 12, Margin = new Thickness(0, 0, 0, 16) });
        var grid = new Grid(); for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new());
        string[] names = { "Низкие · 20–250 Гц", "Средние · 250–2000 Гц", "Высокие · 2–20 кГц" };
        double[] values = { p.Low, p.Mid, p.High };
        for (int i = 0; i < 3; i++)
        {
            int band = i; var column = new StackPanel { Margin = new Thickness(0, 0, i == 2 ? 0 : 18, 0) }; Grid.SetColumn(column, i);
            Slider(column, names[i], values[i], 0, 200, v => { if (band == 0) p.Low = v; else if (band == 1) p.Mid = v; else p.High = v; });
            var meter = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, Value = levels[i] * 100 }; meters.Add(meter); column.Children.Add(meter); grid.Children.Add(column);
        }
        eq.Children.Add(grid);
    }
    void ChooseColor()
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(settings.Color.R, settings.Color.G, settings.Color.B) };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) { settings.Color = new(dialog.Color.R, dialog.Color.G, dialog.Color.B); BuildPage(); Apply(); }
    }
    async Task RefreshSources(bool rebuild = true)
    {
        try
        {
            var sources = await Task.Run(() => (AudioSource.Outputs(), ScreenSource.Displays()));
            if (closing) return;
            audioDevices = sources.Item1; displays = sources.Item2;
            if (settings.Display == "Основной дисплей") settings.Display = "";
            if (!displays.Any(d => d.Id == settings.Display))
            {
                var oldDisplay = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => settings.Display.Contains($"({s.Bounds.Left}, {s.Bounds.Top})"));
                if (oldDisplay != null) settings.Display = oldDisplay.DeviceName;
            }
            if (rebuild) { BuildPage(); Apply(); engine.Restart(); Status("Список звуковых выходов и дисплеев обновлён."); }
            else if (mode is LightMode.Music or LightMode.Spectrum or LightMode.Ambient) { BuildPage(); Apply(); }
        }
        catch (Exception ex) { Status("Не удалось обновить устройства: " + ex.Message); App.Log(ex); }
    }
    void AddIp(object? sender, RoutedEventArgs e)
    {
        var protocol=(AddProtocol.SelectedItem as ProtocolChoice)?.Key??ProtocolKind.Auto;
        if(protocol==ProtocolKind.OpenRgb){ConfigurePc(sender,e);return;}
        string text=IpInput.Text.Trim();
        if(!DeviceEntry.ValidAddress(text,protocol)){Status(DeviceEntry.IsBle(protocol)?"Введите MAC Bluetooth, например AA:BB:CC:DD:EE:FF.":"Введите IPv4-адрес, например 192.168.1.50.");IpInput.Focus();return;}
        string ip;
        if(DeviceEntry.IsBle(protocol)){BluetoothConnection.TryAddress(text,out var mac);ip=BluetoothConnection.FormatAddress(mac);}else ip=IPAddress.Parse(text).ToString();
        if(!devices.Any(d=>d.Ip==ip))devices.Add(new(){Ip=ip,Name=ProtocolName(protocol),Protocol=protocol});
        IpInput.Clear(); Status("Контроллер добавлен. Подключите выбранные устройства.");
    }
    void IpKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddIp(sender, e); }
    async void Scan(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false; Status("Поиск контроллеров в локальной сети…");
        try
        {
            var lists=await Task.WhenAll(DeviceDiscovery.ScanAsync(lifetime.Token),ExtraWifiDiscovery.ScanAsync(lifetime.Token));
            var found=lists.SelectMany(x=>x).DistinctBy(d=>d.Ip).ToArray();
            if (closing) return;
            foreach (var item in found) { var existing = devices.FirstOrDefault(d => d.Ip == item.Ip); if (existing == null) devices.Add(item); else existing.Name = item.Name; }
            Status(found.Length > 0 ? $"Найдено: {found.Length}. WLED/Tuya можно добавить по IP." : "Устройства не найдены. Можно добавить IP вручную.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Ошибка поиска: " + ex.Message); App.Log(ex); }
        finally { if (!closing) ScanButton.IsEnabled = true; }
    }
    void ConfigurePc(object? sender, RoutedEventArgs e)
    {
        var dialog=new PcLightingWindow { Owner=this };
        if(dialog.ShowDialog()==true)
        {
            foreach(var item in dialog.Added)
            {
                var existing=devices.FirstOrDefault(d=>d.Ip==item.Ip);
                if(existing==null) devices.Add(item);
                else { existing.Name=item.Name; existing.OpenRgbZoneName=item.OpenRgbZoneName; }
            }
            Status("Компоненты ПК добавлены. Подключите выбранные устройства.");
        }
    }
    void SelectAll(object sender, RoutedEventArgs e) { DeviceGrid.CommitEdit(); foreach (var d in devices) d.Selected = true; }
    async void ScanBluetooth(object sender,RoutedEventArgs e)
    {
        BleScanButton.IsEnabled=false;Status("Поиск совместимых Bluetooth LE контроллеров…");
        try
        {
            var found=await BluetoothConnection.ScanAsync(lifetime.Token);
            if(closing)return;
            foreach(var d in found)if(!devices.Any(x=>x.Ip==d.Ip))devices.Add(d);
            Status($"Bluetooth: найдено {found.Length}. Ищутся ELK-BLEDOM и Triones/LED BLE; неизвестный контроллер можно добавить по MAC.");
        }
        catch(OperationCanceledException){}
        catch(Exception ex){Status(ex.Message);}
        finally{if(!closing)BleScanButton.IsEnabled=true;}
    }
    void ConfigureTuya(object sender,RoutedEventArgs e)
    {
        if(DeviceGrid.SelectedItem is not DeviceEntry d){Status("Выделите строку контроллера Tuya в таблице.");return;}
        if(DeviceEntry.IsBle(d.Protocol)){Status("Настройка Tuya LAN предназначена для Wi-Fi контроллеров.");return;}
        if(new TuyaSettingsWindow(d){Owner=this}.ShowDialog()==true)Status("Настройки Tuya сохранены. Подключите выбранные устройства заново.");
    }
    void SelectNone(object sender, RoutedEventArgs e) { DeviceGrid.CommitEdit(); foreach (var d in devices) d.Selected = false; }
    void RemoveDevice(object sender, RoutedEventArgs e) { if (DeviceGrid.SelectedItem is DeviceEntry d) devices.Remove(d); Status("Список изменён. Подключите выбранные, чтобы обновить группу."); }
    async void Connect(object sender, RoutedEventArgs e)
    {
        DeviceGrid.CommitEdit(DataGridEditingUnit.Cell, true); DeviceGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = devices.Where(d => d.Selected).ToArray();
        if (selected.Length == 0) { Status("Отметьте хотя бы один контроллер."); DevicePanel.IsExpanded = true; return; }
        if(selected.Any(d=>d.Protocol==ProtocolKind.Tuya&&(d.DeviceId.Length==0||d.ProtectedKey.Length==0))){Status("Для Tuya сначала задайте Device ID и local_key кнопкой «Настройка Tuya».");return;}
        if(selected.Any(d=>!DeviceEntry.ValidAddress(d.Ip,d.Protocol))){Status("IP/MAC не соответствует выбранному семейству контроллера.");return;}
        if(OpenRgbConnection.Overlaps(selected)){Status("OpenRGB: выберите весь компонент либо отдельные зоны, а не оба варианта сразу.");return;}
        if (connecting) return; connecting = true; ConnectButton.IsEnabled = false;
        int generation = ++connectionGeneration;
        try
        {
            foreach (var d in devices) d.Status = d.Selected ? "Подключение…" : "Не выбран";
            await group.ConnectAsync(selected, (ip, status) => OnUi(() =>
            {
                if (generation != connectionGeneration) return;
                var entry = devices.FirstOrDefault(d => d.Ip == ip); if (entry != null) entry.Status = status;
                UpdateGroupStatus();
            }));
            Apply(); engine.Restart(); Status("Группа подключается. При обрыве связи повторное подключение выполняется автоматически.");
        }
        catch (Exception ex) { Status("Ошибка подключения: " + ex.Message); App.Log(ex); }
        finally { connecting = false; if (!closing) ConnectButton.IsEnabled = true; }
    }
    void UpdateGroupStatus()
    {
        int connected = devices.Count(d => d.Status.StartsWith("Подключён") || d.Status is "Включён" or "Выключен");
        int waiting = devices.Count(d => d.Status.StartsWith("Нет связи") || d.Status == "Переподключение…");
        GroupText.Text = $"Подключено: {connected} из {group.Count}" + (waiting > 0 ? $" · восстанавливают связь: {waiting}" : "");
    }
    async void Disconnect(object sender, RoutedEventArgs e) { connectionGeneration++; await group.DisposeAsync(); foreach (var d in devices) d.Status = "Не подключён"; GroupText.Text = "Группа отключена"; Status("Управление остановлено. На лентах остаётся последний цвет."); }
    void PowerOn(object sender, RoutedEventArgs e) { if (group.Count == 0) { Status("Сначала подключите контроллеры."); return; } engine.Power(true); Status("Команда включения отправлена группе."); }
    void PowerOff(object sender, RoutedEventArgs e) { engine.Power(false); Status("Команда выключения отправлена группе."); }
    async void CloseWindow(object? sender, CancelEventArgs e)
    {
        if (closing) return; e.Cancel = true; closing = true; ready = false; lifetime.Cancel();
        if (!testing) { try { settings.Devices = devices.ToList(); SettingsStore.Save(settings); } catch (Exception ex) { App.Log(ex); } }
        await engine.DisposeAsync(); await group.DisposeAsync();
        Close();
    }
    public async Task RunUiTests()
    {
        await Task.Delay(250);
        audioDevices = new[] { new AudioEndpoint("", "Основной выход Windows"), new AudioEndpoint("test-speaker", "Тестовые колонки") };
        displays = new[] { new DisplayEndpoint("", "Основной дисплей"), new DisplayEndpoint("test-display", "Дисплей 2 · 1920×1080") };
        for (int i = 0; i < 5; i++)
        {
            Navigation.SelectedIndex = i; UpdateLayout(); await Task.Delay(30);
            SelfTests.Assert(Snapshot().Mode == (LightMode)i, "Navigation selects the engine mode");
            SelfTests.Assert(PageContent.Children.Count > 0 && PageContent.ActualWidth >= 500, "Page has visible controls");
        }
        Navigation.SelectedIndex = 2; settings.Music.Style = MusicStyle.Strobe; BuildPage(); UpdateLayout();
        var sliders = FindVisual<Slider>(PageContent).ToArray();
        SelfTests.Assert(sliders.Length == 6, "Strobe controls contain brightness, duration, gain and EQ");
        sliders[0].Value = 83; SelfTests.Assert(settings.Music.FlashBrightness == 83, "Slider changes native settings");
        System.Windows.Controls.Slider.IncreaseSmall.Execute(null, sliders[0]); SelfTests.Assert(settings.Music.FlashBrightness == 84, "Slider supports standard keyboard commands");
        settings.Spectrum.Gain = 4; settings.Music.Gain = 19; Navigation.SelectedIndex = 3;
        SelfTests.Assert(Snapshot().Gain == 4, "Spectrum has independent settings"); Navigation.SelectedIndex = 2;
        SelfTests.Assert(Snapshot().Gain == 19, "Music profile is preserved");
        Navigation.SelectedIndex = 4; UpdateLayout();
        SelfTests.Assert(FindVisual<ComboBox>(PageContent).Count() == 1 && FindVisual<Slider>(PageContent).Count() == 1, "Ambient page only contains display and brightness");
        IpInput.Text = "192.0.2.5"; AddIp(null, new RoutedEventArgs()); SelfTests.Assert(devices.Count == 1, "Manual controller entry");
        AddIp(null, new RoutedEventArgs()); SelfTests.Assert(devices.Count == 1, "Invalid entry is rejected");
        AddProtocol.SelectedItem=((System.Collections.IEnumerable)AddProtocol.ItemsSource).Cast<ProtocolChoice>().First(x=>x.Key==ProtocolKind.ElkBledom);
        IpInput.Text="AA-BB-CC-DD-EE-FF";AddIp(null,new RoutedEventArgs());SelfTests.Assert(devices.Last().Ip=="AA:BB:CC:DD:EE:FF"&&devices.Last().Protocol==ProtocolKind.ElkBledom,"BLE manual controller entry");
        var tuyaDialog=new TuyaSettingsWindow(new DeviceEntry{Ip="192.0.2.6",Protocol=ProtocolKind.Tuya}){Owner=this};tuyaDialog.Show();tuyaDialog.UpdateLayout();
        SelfTests.Assert(FindVisual<PasswordBox>(tuyaDialog).Count()==1,"Tuya key uses masked password control");tuyaDialog.Close();
        var pcDialog=new PcLightingWindow { Owner=this };pcDialog.Show();pcDialog.UpdateLayout();
        SelfTests.Assert(FindVisual<TextBox>(pcDialog).Count()==2,"PC lighting server and port controls");
        SelfTests.Assert(FindVisual<Button>(pcDialog).Any(b=>Equals(b.Content,"Найти компоненты")),"PC lighting discovery button");
        var pcPath=Environment.GetEnvironmentVariable("RGBROOM_PC_PREVIEW");
        if(!string.IsNullOrEmpty(pcPath))
        {
            var pcBitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)pcDialog.ActualWidth,(int)pcDialog.ActualHeight,96,96,PixelFormats.Pbgra32);
            pcBitmap.Render(pcDialog);var pcPng=new System.Windows.Media.Imaging.PngBitmapEncoder();pcPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(pcBitmap));
            using var pcStream=System.IO.File.Create(pcPath);pcPng.Save(pcStream);
        }
        pcDialog.Close();
        SelfTests.Report("PASS: PC lighting dialog, server/port controls and discovery action");
        DevicePanel.IsExpanded = true; Width = 920; Height = 700; UpdateLayout();
        SelfTests.Assert(DeviceGrid.ActualWidth > 700, "Controller grid fits minimum window");
        DevicePanel.IsExpanded = false; Width = 1060; Height = 820; Navigation.SelectedIndex = 0; UpdateLayout();
        await Task.Delay(300); UpdateLayout();
        SelfTests.Assert(TryFindResource("DefaultButtonStyle") is Style, "Native Fluent button resources");
        var brightnessSlider = FindVisual<Slider>(PageContent).First(s => s.IsVisible);
        var track = FindVisual<System.Windows.Controls.Primitives.Track>(brightnessSlider).First();
        SelfTests.Assert(track.DecreaseRepeatButton.ActualWidth + track.IncreaseRepeatButton.ActualWidth > 500, "Slider track fills the card");
        Status("Выберите контроллеры и подключите группу.");
        SelfTests.Report("PASS: native Fluent window, five mode pages, independent profiles, sliders, strobe controls, monitor selection, controller grid and compact resize");
        // Render the app's own visual tree only; no desktop capture is performed.
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this); var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var path = Environment.GetEnvironmentVariable("MAGICHOME_UI_PREVIEW"); if (!string.IsNullOrEmpty(path)) { using var stream = System.IO.File.Create(path); png.Save(stream); }
    }
    static IEnumerable<T> FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T t) yield return t;
            foreach (var descendant in FindVisual<T>(child)) yield return descendant;
        }
    }
}
