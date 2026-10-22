using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace MagicHomeNET;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => { Log(args.Exception); MessageBox.Show(args.Exception.Message, "RGBroom", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        if (e.Args.Contains("--self-test"))
        {
            try { await SelfTests.Run(); Shutdown(0); }
            catch (Exception ex) { Log(ex); SelfTests.Report("FAIL: " + ex); Shutdown(1); }
            return;
        }
        var window = new MainWindow(e.Args.Contains("--ui-test"));
        MainWindow = window;
        window.Show();
        if (e.Args.Contains("--ui-test"))
        {
            try { await window.RunUiTests(); window.Close(); Shutdown(0); }
            catch (Exception ex) { SelfTests.Report("UI FAIL: " + ex); Log(ex); Shutdown(1); }
        }
    }
    public static void Log(Exception ex)
    {
        if (SelfTests.Active) return;
        try { Directory.CreateDirectory(SettingsStore.DirectoryPath); File.AppendAllText(Path.Combine(SettingsStore.DirectoryPath, "errors.log"), DateTimeOffset.Now + " " + ex + Environment.NewLine); } catch { }
    }
}
