using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MagicHomeNET;
public static class AdapterTests
{
    public static async Task Run()
    {
        SelfTests.Assert(Convert.ToHexString(BluetoothConnection.ColorPacket(ProtocolKind.Triones,new(1,2,3)))=="5601020300F0AA","Triones RGB bytes");
        SelfTests.Assert(Convert.ToHexString(BluetoothConnection.ColorPacket(ProtocolKind.ElkBledom,new(1,2,3)))=="7E00050301020300EF","ELK legacy RGB bytes");
        SelfTests.Assert(Convert.ToHexString(BluetoothConnection.ColorPacket(ProtocolKind.ElkBledom,new(1,2,3),true))=="7E0705030102030AEF","ELK handle 13 RGB bytes");
        SelfTests.Assert(Convert.ToHexString(BluetoothConnection.PowerPacket(ProtocolKind.Triones,false))=="CC2433","Triones power");
        SelfTests.Assert(BluetoothConnection.TryAddress("AA:BB:CC:DD:EE:FF",out var mac)&&BluetoothConnection.FormatAddress(mac)=="AA:BB:CC:DD:EE:FF","BLE MAC normalization");
        SelfTests.Assert(!DeviceEntry.ValidAddress("192.0.2.5",ProtocolKind.Triones),"BLE rejects IP address");
        var config=new UserSettings{Devices=new(){new(){Ip=OperatingSystem.IsMacOS()?"11111111-2222-3333-4444-555555555555":"AA:BB:CC:DD:EE:FF",Protocol=ProtocolKind.ElkBledom}}};config.Validate();SelfTests.Assert(config.Devices.Count==1,"BLE settings survive reload");
        byte[] key=Encoding.ASCII.GetBytes("0123456789abcdef"),data=Encoding.UTF8.GetBytes("RGBroom synthetic protocol test");
        foreach(string version in new[]{"3.3","3.4","3.5"})
        {
            byte[] frame=TuyaCodec.Pack(5,8,data,key,version,true,Enumerable.Range(0,12).Select(i=>(byte)i).ToArray());
            SelfTests.Assert(TuyaCodec.Unpack(frame,key,version).Payload.SequenceEqual(data),"Tuya codec roundtrip "+version);
            frame[frame.Length-8]^=1;
            bool rejected=false;try{TuyaCodec.Unpack(frame,key,version);}catch{rejected=true;}SelfTests.Assert(rejected,"Tuya rejects corrupted authentication "+version);
            await Tuya(version);
        }
        SelfTests.Assert(TuyaConnection.ColorValue(new(255,0,0),false)=="000003e803e8","Tuya modern HSV");
        SelfTests.Assert(TuyaConnection.ColorValue(new(0,255,0),true)=="00ff000078ffff","Tuya legacy RGBHSV");
        string protectedKey=LocalSecrets.Protect("0123456789abcdef");SelfTests.Assert(!protectedKey.Contains("0123456789abcdef")&&LocalSecrets.Reveal(protectedKey)=="0123456789abcdef","Platform local_key protection");
#if MACOS
        LocalSecrets.Delete(protectedKey);
#endif
        await Wled();await Yeelight();
        SelfTests.Report("PASS: ELK/Triones BLE packets and addresses; Tuya 3.3/3.4/3.5 encrypted sessions, authentication, DP mapping, color/power/heartbeat; protected local_key; WLED HTTP; Yeelight reverse music TCP");
    }
    static async Task Tuya(string version)
    {
        byte[] original=Encoding.ASCII.GetBytes("0123456789abcdef"),session=original;
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout=new CancellationTokenSource(12000);var token=timeout.Token;var commands=new ConcurrentQueue<uint>();
        var server=Task.Run(async()=>
        {
            using var client=await listener.AcceptTcpClientAsync(token);var stream=client.GetStream();byte[]? local=null;byte[] remote=Enumerable.Range(30,16).Select(x=>(byte)x).ToArray();uint seq=100;
            while(true)
            {
                int size=version=="3.5"?18:16;byte[] header=new byte[size];int first=await stream.ReadAsync(header.AsMemory(0,1),token);if(first==0)break;
                await stream.ReadExactlyAsync(header.AsMemory(1),token);int count=(int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(size-4));
                byte[] frame=new byte[size+count+(version=="3.5"?4:0)];header.CopyTo(frame,0);await stream.ReadExactlyAsync(frame.AsMemory(size),token);
                var message=TuyaCodec.Unpack(frame,session,version,false);commands.Enqueue(message.Command);byte[] plain=message.Payload;
                if(version=="3.3"&&plain.Length>=15&&plain[0]=='3')plain=plain[15..];
                if(version!="3.5"&&plain.Length>0)plain=TuyaCodec.Ecb(plain,session,false);
                if(plain.Length>=15&&plain[0]=='3')plain=plain[15..];
                byte[] answer=Array.Empty<byte>();uint responseCommand=message.Command;
                if(message.Command==3){local=plain;answer=remote.Concat(HMACSHA256.HashData(original,local)).ToArray();responseCommand=4;}
                else if(message.Command==5)
                {
                    SelfTests.Assert(plain.SequenceEqual(HMACSHA256.HashData(original,remote)),"Tuya finishes nonce handshake");
                    var mixed=local!.Zip(remote,(a,b)=>(byte)(a^b)).ToArray();
                    if(version=="3.4")session=TuyaCodec.Ecb(mixed,original,true,false);
                    else {session=new byte[16];byte[] tag=new byte[16];using var aes=new AesGcm(original,16);aes.Encrypt(local.AsSpan(0,12),mixed,session,tag);}
                    continue;
                }
                else if(message.Command is 10 or 16) answer=Encoding.UTF8.GetBytes("{\"dps\":{\"20\":true,\"21\":\"colour\",\"24\":\"000003e803e8\"}}");
                else if(message.Command is 7 or 13)
                {
                    using var json=JsonDocument.Parse(plain);var root=json.RootElement;if(root.TryGetProperty("data",out var inner))root=inner;
                    SelfTests.Assert(root.TryGetProperty("dps",out _),"Tuya control includes DPS");
                }
                if(version!="3.5"&&answer.Length>0)answer=TuyaCodec.Ecb(answer,session,true);
                await stream.WriteAsync(TuyaCodec.Pack(++seq,responseCommand,answer,session,version,true),token);
            }
        });
        var entry=new DeviceEntry{Ip="127.0.0.1",Protocol=ProtocolKind.Tuya,DeviceId="rgbroom-test-only",ProtectedKey=LocalSecrets.Protect("0123456789abcdef"),TuyaVersion=version};
        await using(var connection=new TuyaConnection(entry,port)){await connection.ConnectAsync(token);await connection.SetColorAsync(new(255,0,0),false,token);await connection.SetPowerAsync(false,token);await connection.PingAsync(token);}
        #if MACOS
        LocalSecrets.Delete(entry.ProtectedKey);
#endif
        await server;listener.Stop();SelfTests.Assert(commands.Contains(9u)&&commands.Any(x=>x is 7 or 13),"Tuya heartbeat and controls "+version);
    }
    static async Task Wled()
    {
        var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();int port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        using var listener=new HttpListener();listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();
        var requests=new ConcurrentQueue<string>();
        var server=Task.Run(async()=>
        {
            for(int i=0;i<4;i++)
            {
                var ctx=await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
                using var reader=new StreamReader(ctx.Request.InputStream);requests.Enqueue(await reader.ReadToEndAsync());
                byte[] bytes=Encoding.UTF8.GetBytes(ctx.Request.HttpMethod=="GET"?"{\"ver\":\"0.15.0\"}":"{\"success\":true}");
                ctx.Response.ContentType="application/json";ctx.Response.ContentLength64=bytes.Length;await ctx.Response.OutputStream.WriteAsync(bytes);ctx.Response.Close();
            }
        });
        await using(var c=new WledConnection("127.0.0.1",port)){await c.ConnectAsync(default);await c.SetColorAsync(new(1,2,3),false,default);await c.SetPowerAsync(false,default);await c.PingAsync(default);}
        await server;SelfTests.Assert(requests.Any(x=>x.Contains("\"col\":[[1,2,3]]"))&&requests.Any(x=>x.Contains("\"on\":false")),"WLED JSON controls");
    }
    static async Task Yeelight()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout=new CancellationTokenSource(8000);var token=timeout.Token;var messages=new ConcurrentQueue<string>();
        var server=Task.Run(async()=>
        {
            using var control=await listener.AcceptTcpClientAsync(token);var stream=control.GetStream();using var reader=new StreamReader(stream,leaveOpen:true);
            Task musicReader=Task.CompletedTask;TcpClient? music=null;
            for(int i=0;i<3;i++)
            {
                string line=(await reader.ReadLineAsync(token))!;using var json=JsonDocument.Parse(line);var root=json.RootElement;
                byte[] reply=Encoding.UTF8.GetBytes($"{{\"id\":{root.GetProperty("id").GetInt32()},\"result\":[\"ok\"]}}\r\n");await stream.WriteAsync(reply,token);
                if(root.GetProperty("method").GetString()=="set_music")
                {
                    var p=root.GetProperty("params");music=new TcpClient();await music.ConnectAsync(p[1].GetString()!,p[2].GetInt32(),token);
                    var m=music;musicReader=Task.Run(async()=>{using var r=new StreamReader(m.GetStream(),leaveOpen:true);for(int n=0;n<2;n++)messages.Enqueue((await r.ReadLineAsync(token))!);});
                }
            }
            await musicReader;music?.Dispose();
        });
        await using(var c=new YeelightConnection("127.0.0.1",port)){await c.ConnectAsync(token);await c.SetColorAsync(new(255,0,0),false,token);await c.SetPowerAsync(false,token);await c.PingAsync(token);}
        await server;listener.Stop();SelfTests.Assert(messages.Any(x=>x.Contains("set_scene"))&&messages.Any(x=>x.Contains("set_power")),"Yeelight music session supports color and power");
    }
}


