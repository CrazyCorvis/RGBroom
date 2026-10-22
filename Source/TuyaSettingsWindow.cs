using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace MagicHomeNET;
public sealed class TuyaSettingsWindow : Window
{
    public TuyaSettingsWindow(DeviceEntry device)
    {
        Title="RGBroom · Tuya / Smart Life";Width=460;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty,"SolidBackgroundFillColorBaseBrush");SetResourceReference(ForegroundProperty,"TextFillColorPrimaryBrush");
        var panel=new StackPanel{Margin=new Thickness(24)};Content=panel;
        panel.Children.Add(new TextBlock{Text=device.Ip,FontSize=20,Margin=new Thickness(0,0,0,16)});
        TextBox Field(string label,string value){panel.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,8,0,4)});var box=new TextBox{Text=value,MinHeight=32};panel.Children.Add(box);return box;}
        var id=Field("Device ID",device.DeviceId);
        panel.Children.Add(new TextBlock{Text="local_key · пустое поле оставляет сохранённый ключ",Margin=new Thickness(0,12,0,4)});
        var key=new PasswordBox{MinHeight=32};panel.Children.Add(key);
        panel.Children.Add(new TextBlock{Text="Версия локального протокола",Margin=new Thickness(0,12,0,4)});
        var version=new ComboBox{ItemsSource=new[]{"3.3","3.4","3.5"},SelectedItem=device.TuyaVersion};panel.Children.Add(version);
        var power=Field("DP питания · 0 = авто (20 или 1)",device.PowerDp.ToString());
        var mode=Field("DP режима · 0 = авто (21 или 2)",device.ModeDp.ToString());
        var color=Field("DP цвета · 0 = авто (24 или 5)",device.ColorDp.ToString());
        var device22=new CheckBox{Content="Запрос DP через CONTROL_NEW (device22)",IsChecked=device.TuyaDevice22,Margin=new Thickness(0,12,0,0)};panel.Children.Add(device22);
        var error=new TextBlock{Text="Ключ защищается Windows DPAPI для текущего пользователя.\nПоддерживается прямой Tuya LAN; не облачный API и не Tuya BLE Mesh.",Margin=new Thickness(0,14,0,14),FontSize=12};panel.Children.Add(error);
        var save=new Button{Content="Сохранить",HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(save);
        save.Click+=(_,_)=>
        {
            if(id.Text.Trim().Length==0){error.Text="Введите Device ID.";return;}
            if(key.Password.Length>0&&Encoding.UTF8.GetByteCount(key.Password)!=16){error.Text="local_key должен содержать 16 байт UTF-8.";return;}
            if(key.Password.Length==0&&device.ProtectedKey.Length==0){error.Text="Введите local_key устройства.";return;}
            if(!int.TryParse(power.Text,out int p)||!int.TryParse(mode.Text,out int m)||!int.TryParse(color.Text,out int c)||p<0||m<0||c<0||p>255||m>255||c>255){error.Text="DP должны быть целыми числами 0–255.";return;}
            device.DeviceId=id.Text.Trim();if(key.Password.Length>0)device.ProtectedKey=LocalSecrets.Protect(key.Password);
            device.TuyaVersion=version.SelectedItem?.ToString()??"3.3";device.TuyaDevice22=device22.IsChecked==true;device.PowerDp=p;device.ModeDp=m;device.ColorDp=c;device.Protocol=ProtocolKind.Tuya;
            DialogResult=true;
        };
    }
}
