using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MagicHomeNET;
public static class ExtraWifiDiscovery
{
    public static async Task<IReadOnlyList<DeviceEntry>> ScanAsync(CancellationToken token)
    {
        using var udp=new UdpClient(AddressFamily.InterNetwork);
        byte[] request=Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1982\r\nMAN: \"ssdp:discover\"\r\nST: wifi_bulb\r\n\r\n");
        await udp.SendAsync(request,new IPEndPoint(IPAddress.Parse("239.255.255.250"),1982),token);
        var found=new Dictionary<string,DeviceEntry>();using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(3000);
        try
        {
            while(true)
            {
                var response=await udp.ReceiveAsync(timeout.Token);string text=Encoding.UTF8.GetString(response.Buffer);
                if(!text.Contains("yeelight://",StringComparison.OrdinalIgnoreCase))continue;
                string ip=response.RemoteEndPoint.Address.ToString();
                string model=text.Split('\n').FirstOrDefault(s=>s.StartsWith("model:",StringComparison.OrdinalIgnoreCase))?.Trim()??"Yeelight LAN";
                found[ip]=new(){Ip=ip,Name=model,Protocol=ProtocolKind.Yeelight};
            }
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){}
        return found.Values.ToArray();
    }
}
