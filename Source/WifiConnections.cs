using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MagicHomeNET;
public static class ConnectionFactory
{
    public static ILightConnection Create(DeviceEntry d) => d.Protocol switch
    {
        ProtocolKind.Wled => new WledConnection(d.Ip),
        ProtocolKind.Yeelight => new YeelightConnection(d.Ip),
        ProtocolKind.ElkBledom or ProtocolKind.Triones => new BluetoothConnection(d.Ip, d.Protocol,d.BluetoothRandom,d.BluetoothElk13),
        ProtocolKind.Tuya => new TuyaConnection(d),
        _ => new TcpLightConnection(d.Ip, d.Protocol)
    };
}
public sealed class WledConnection : ILightConnection
{
    static readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(2)};
    readonly string baseUrl;
    public string Description => "WLED · Wi-Fi";
    public WledConnection(string host, int port=80) {baseUrl=$"http://{host}:{port}/";}
    public Task ConnectAsync(CancellationToken token) => PingAsync(token);
    public async Task PingAsync(CancellationToken token)
    {
        using var response=await http.GetAsync(baseUrl+"json/info",token); response.EnsureSuccessStatusCode();
        using var json=await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token),cancellationToken:token);
        if(!json.RootElement.TryGetProperty("ver",out _)) throw new IOException("Устройство не ответило как WLED.");
    }
    public static object ColorState(Rgb c) => new { bri=255, seg=new[] { new { id=0, fx=0, col=new[] { new[] { (int)c.R,(int)c.G,(int)c.B } } } } };
    async Task Send(object payload,CancellationToken token) { using var response=await http.PostAsJsonAsync(baseUrl+"json/state",payload,token); response.EnsureSuccessStatusCode(); }
    public Task SetColorAsync(Rgb c,bool persist,CancellationToken token)=>Send(ColorState(c),token);
    public Task SetPowerAsync(bool on,CancellationToken token)=>Send(new { on },token);
    // Keep the HttpClient reusable across connection retries.
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
public sealed class YeelightConnection : ILightConnection
{
    readonly string host; readonly int port;
    TcpClient? control, music; StreamReader? reader;
    int sequence;
    public string Description=>"Yeelight LAN · Wi-Fi";
    public YeelightConnection(string host,int port=55443) {this.host=host;this.port=port;}
    public static string Command(int id,string method,params object[] args)=>JsonSerializer.Serialize(new { id,method,@params=args })+"\r\n";
    async Task<JsonElement> Request(string method,CancellationToken token,params object[] args)
    {
        int id=++sequence;
        await control!.GetStream().WriteAsync(Encoding.UTF8.GetBytes(Command(id,method,args)),token);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(2000);
        while(true)
        {
            string? line=await reader!.ReadLineAsync(timeout.Token);
            if(line==null)throw new IOException("Yeelight закрыл соединение.");
            using var response=JsonDocument.Parse(line);
            if(!response.RootElement.TryGetProperty("id",out var r)||r.GetInt32()!=id)continue;
            if(response.RootElement.TryGetProperty("error",out var error))throw new IOException("Yeelight: "+error);
            return response.RootElement.Clone();
        }
    }
    public async Task ConnectAsync(CancellationToken token)
    {
        control?.Dispose();music?.Dispose();reader?.Dispose();
        control=new TcpClient { NoDelay=true };
        await control.ConnectAsync(host,port,token).AsTask().WaitAsync(TimeSpan.FromSeconds(3),token);
        reader=new StreamReader(control.GetStream(),Encoding.UTF8,leaveOpen:true);
        await Request("get_prop",token,"power","rgb");
        var local=((IPEndPoint)control.Client.LocalEndPoint!).Address;
        if(local.IsIPv4MappedToIPv6)local=local.MapToIPv4();
        var listener=new TcpListener(local,0);listener.Start();
        try
        {
            int callbackPort=((IPEndPoint)listener.LocalEndpoint).Port;
            await Request("set_music",token,1,local.ToString(),callbackPort);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(4000);
            // Only accept the controller we initiated the session with.
            while(true)
            {
                var peer=await listener.AcceptTcpClientAsync(timeout.Token);
                if(((IPEndPoint)peer.Client.RemoteEndPoint!).Address.Equals(IPAddress.Parse(host))) {music=peer;music.NoDelay=true;break;}
                peer.Dispose();
            }
        }
        finally {listener.Stop();}
    }
    async Task Send(string method,CancellationToken token,params object[] args)
    {
        if(music==null)throw new IOException("Нет music-соединения Yeelight.");
        await music.GetStream().WriteAsync(Encoding.UTF8.GetBytes(Command(++sequence,method,args)),token).AsTask().WaitAsync(TimeSpan.FromSeconds(2),token);
    }
    public Task SetColorAsync(Rgb c,bool persist,CancellationToken token)=>Send("set_scene",token,"color",(c.R<<16)|(c.G<<8)|c.B,c==Rgb.Black?1:100);
    public Task SetPowerAsync(bool on,CancellationToken token)=>Send("set_power",token,on?"on":"off","sudden",0);
    public async Task PingAsync(CancellationToken token)
    {
        if(music==null||music.Client.Poll(0,SelectMode.SelectRead)&&music.Client.Available==0)throw new IOException("Music-канал Yeelight потерян.");
        await Request("get_prop",token,"power");
    }
    public ValueTask DisposeAsync(){music?.Dispose();music=null;reader?.Dispose();reader=null;control?.Dispose();control=null;return ValueTask.CompletedTask;}
}
