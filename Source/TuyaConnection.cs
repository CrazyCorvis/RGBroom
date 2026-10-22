using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MagicHomeNET;
public static class LocalSecrets
{
    static readonly byte[] Entropy=Encoding.UTF8.GetBytes("RGBroom.Tuya.local_key");
    public static string Protect(string value)=>Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),Entropy,DataProtectionScope.CurrentUser));
    public static string Reveal(string value)
    {
        try{return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),Entropy,DataProtectionScope.CurrentUser));}
        catch{throw new IOException("Не удалось прочитать local_key. Введите его заново в настройках Tuya.");}
    }
}
public static class TuyaCodec
{
    public static byte[] Ecb(byte[] bytes,byte[] key,bool encrypt,bool pad=true)
    {
        using var aes=Aes.Create();aes.Key=key;
        return encrypt?aes.EncryptEcb(bytes,pad?PaddingMode.PKCS7:PaddingMode.None):aes.DecryptEcb(bytes,pad?PaddingMode.PKCS7:PaddingMode.None);
    }
    public static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc=0xffffffff;foreach(byte b in bytes){crc^=b;for(int i=0;i<8;i++)crc=(crc>>1)^((crc&1)==1?0xedb88320u:0);}return ~crc;
    }
    public static byte[] Pack(uint sequence,uint command,byte[] payload,byte[] key,string version,bool reply=false,byte[]? nonce=null)
    {
        if(reply)payload=new byte[4].Concat(payload).ToArray();
        if(version=="3.5")
        {
            nonce??=RandomNumberGenerator.GetBytes(12);
            byte[] packet=new byte[18+12+payload.Length+16+4];
            BinaryPrimitives.WriteUInt32BigEndian(packet,0x6699);BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(6),sequence);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(10),command);BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(14),(uint)(12+payload.Length+16));
            nonce.CopyTo(packet,18);using var aes=new AesGcm(key,16);
            aes.Encrypt(nonce,payload,packet.AsSpan(30,payload.Length),packet.AsSpan(30+payload.Length,16),packet.AsSpan(4,14));
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(packet.Length-4),0x9966);return packet;
        }
        int integrity=version=="3.4"?32:4;
        byte[] frame=new byte[16+payload.Length+integrity+4];
        BinaryPrimitives.WriteUInt32BigEndian(frame,0x55aa);BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4),sequence);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8),command);BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(12),(uint)(payload.Length+integrity+4));
        payload.CopyTo(frame,16);int footer=16+payload.Length;
        if(version=="3.4")HMACSHA256.HashData(key,frame.AsSpan(0,footer)).CopyTo(frame,footer);
        else BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(footer),Crc(frame.AsSpan(0,footer)));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(frame.Length-4),0xaa55);return frame;
    }
    public static (uint Command,byte[] Payload) Unpack(byte[] packet,byte[] key,string version,bool reply=true)
    {
        int offset;uint command;byte[] payload;
        if(version=="3.5")
        {
            if(packet.Length<50||BinaryPrimitives.ReadUInt32BigEndian(packet)!=0x6699||BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(packet.Length-4))!=0x9966)throw new IOException("Неверный пакет Tuya 3.5.");
            int count=checked((int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(14)));
            if(count!=packet.Length-22)throw new IOException("Неверная длина Tuya 3.5.");
            payload=new byte[count-28];using var aes=new AesGcm(key,16);
            aes.Decrypt(packet.AsSpan(18,12),packet.AsSpan(30,payload.Length),packet.AsSpan(30+payload.Length,16),payload,packet.AsSpan(4,14));
            command=BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(10));offset=0;
        }
        else
        {
            int integrity=version=="3.4"?32:4;
            if(packet.Length<20+integrity||BinaryPrimitives.ReadUInt32BigEndian(packet)!=0x55aa||BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(packet.Length-4))!=0xaa55)throw new IOException("Неверный пакет Tuya.");
            int count=checked((int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12)));
            if(count!=packet.Length-16)throw new IOException("Неверная длина Tuya.");
            int end=packet.Length-integrity-4;
            bool valid=version=="3.4"?CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key,packet.AsSpan(0,end)),packet.AsSpan(end,32)):
                Crc(packet.AsSpan(0,end))==BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(end));
            if(!valid)throw new IOException("Ошибка проверки целостности Tuya.");
            command=BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8));payload=packet.AsSpan(16,end-16).ToArray();offset=0;
        }
        if(reply)
        {
            if(payload.Length<4||BinaryPrimitives.ReadUInt32BigEndian(payload)!=0)throw new IOException("Tuya вернул ошибку команды или local_key.");
            offset=4;
        }
        return(command,payload.AsSpan(offset).ToArray());
    }
}
public sealed class TuyaConnection : ILightConnection
{
    readonly DeviceEntry config;readonly int port;
    TcpClient? client;byte[] key=Array.Empty<byte>();uint sequence;
    int powerDp,modeDp,colorDp;
    public string Description=>"Tuya LAN "+config.TuyaVersion;
    public TuyaConnection(DeviceEntry config,int port=6668){this.config=config;this.port=port;}
    async Task Send(uint command,byte[] plaintext,CancellationToken token)
    {
        byte[] payload;
        if(config.TuyaVersion=="3.5"||config.TuyaVersion=="3.3"&&plaintext.Length==0)payload=plaintext;
        else if(config.TuyaVersion=="3.3"&&plaintext.Length>=15&&plaintext[0]=='3'&&plaintext[1]=='.')payload=plaintext[..15].Concat(TuyaCodec.Ecb(plaintext[15..],key,true)).ToArray();
        else payload=TuyaCodec.Ecb(plaintext,key,true);
        await client!.GetStream().WriteAsync(TuyaCodec.Pack(++sequence,command,payload,key,config.TuyaVersion),token);
    }
    async Task<(uint Command,byte[] Payload)> Receive(CancellationToken token)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(2500);
        int headerSize=config.TuyaVersion=="3.5"?18:16;byte[] header=new byte[headerSize];
        await client!.GetStream().ReadExactlyAsync(header,timeout.Token);
        int count=checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(headerSize-4)));
        if(count<8||count>65536)throw new IOException("Неверная длина ответа Tuya.");
        byte[] packet=new byte[headerSize+count+(config.TuyaVersion=="3.5"?4:0)];header.CopyTo(packet,0);
        await client.GetStream().ReadExactlyAsync(packet.AsMemory(headerSize),timeout.Token);
        var message=TuyaCodec.Unpack(packet,key,config.TuyaVersion);
        byte[] data=message.Payload;
        if(config.TuyaVersion!="3.5"&&data.Length>0)
        {
            if(config.TuyaVersion=="3.3"&&data.Length>=15&&data[0]==(byte)'3'&&data[1]==(byte)'.')data=data[15..];
            if(data.Length>0&&!(config.TuyaVersion=="3.3"&&data[0]==(byte)'{'))data=TuyaCodec.Ecb(data,key,false);
        }
        if(data.Length>=15&&data[0]==(byte)'3'&&data[1]==(byte)'.')data=data[15..];
        return(message.Command,data);
    }
    async Task<byte[]> Exchange(uint command,byte[] plain,CancellationToken token)
    {
        await Send(command,plain,token);
        for(int i=0;i<32;i++){var r=await Receive(token);if(r.Command==command||r.Command==8||command==3&&r.Command==4)return r.Payload;}
        throw new IOException("Tuya не подтвердил команду.");
    }
    public async Task ConnectAsync(CancellationToken token)
    {
        client?.Dispose();
        if(config.DeviceId.Length==0||config.ProtectedKey.Length==0)throw new IOException("Откройте настройку Tuya и введите Device ID и local_key.");
        key=Encoding.UTF8.GetBytes(LocalSecrets.Reveal(config.ProtectedKey));
        if(key.Length!=16)throw new IOException("local_key должен содержать 16 байт UTF-8.");
        if(config.TuyaVersion is not ("3.3" or "3.4" or "3.5"))throw new IOException("Поддерживаются версии Tuya 3.3, 3.4, 3.5.");
        client=new TcpClient{NoDelay=true};await client.ConnectAsync(config.Ip,port,token).AsTask().WaitAsync(TimeSpan.FromSeconds(3),token);
        if(config.TuyaVersion!="3.3")
        {
            byte[] local=RandomNumberGenerator.GetBytes(16),response=await Exchange(3,local,token);
            if(response.Length<48||!CryptographicOperations.FixedTimeEquals(response.AsSpan(16,32),HMACSHA256.HashData(key,local)))throw new IOException("Проверка local_key Tuya не пройдена.");
            byte[] remote=response[..16];await Send(5,HMACSHA256.HashData(key,remote),token);
            byte[] mixed=local.Zip(remote,(a,b)=>(byte)(a^b)).ToArray();
            if(config.TuyaVersion=="3.4")key=TuyaCodec.Ecb(mixed,key,true,false);
            else {byte[] cipher=new byte[16],tag=new byte[16];using var gcm=new AesGcm(key,16);gcm.Encrypt(local.AsSpan(0,12),mixed,cipher,tag);key=cipher;}
        }
        byte[] status=await Query(token);
        using var json=JsonDocument.Parse(status);
        JsonElement root=json.RootElement;
        if(root.TryGetProperty("data",out var wrapped))root=wrapped;
        if(!root.TryGetProperty("dps",out var dps))throw new IOException("Tuya не вернул список DP. Проверьте версию протокола и DP-профиль.");
        powerDp=config.PowerDp>0?config.PowerDp:dps.TryGetProperty("20",out _)?20:1;
        modeDp=config.ModeDp>0?config.ModeDp:dps.TryGetProperty("21",out _)?21:2;
        colorDp=config.ColorDp>0?config.ColorDp:dps.TryGetProperty("24",out _)?24:5;
        if(config.PowerDp==0&&!dps.TryGetProperty(powerDp.ToString(),out _)||config.ColorDp==0&&!dps.TryGetProperty(colorDp.ToString(),out _))throw new IOException("DP-профиль не соответствует RGB-контроллеру. Укажите DP вручную.");
    }
    byte[] JsonPayload(uint command,object content)
    {
        byte[] json=JsonSerializer.SerializeToUtf8Bytes(content);
        bool header=command is not (3 or 4 or 5 or 9 or 10 or 16 or 18);
        if(!header)return json;
        byte[] marker=Encoding.ASCII.GetBytes(config.TuyaVersion).Concat(new byte[12]).ToArray();
        return marker.Concat(json).ToArray();
    }
    async Task<byte[]> Query(CancellationToken token)
    {
        uint cmd=config.TuyaVersion=="3.3"?10u:16u;
        if(config.TuyaDevice22)
        {
            cmd=13;
            var requested=new Dictionary<string,object?>();
            foreach(int dp in new[]{config.PowerDp>0?config.PowerDp:20,config.ModeDp>0?config.ModeDp:21,config.ColorDp>0?config.ColorDp:24})requested[dp.ToString()]=null;
            return await Exchange(cmd,JsonPayload(cmd,new{devId=config.DeviceId,uid=config.DeviceId,t=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),dps=requested}),token);
        }
        object payload=config.TuyaVersion=="3.3"?new{gwId=config.DeviceId,devId=config.DeviceId,uid=config.DeviceId,t=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()} : new { };
        return await Exchange(cmd,JsonPayload(cmd,payload),token);
    }
    async Task Set(Dictionary<string,object> dps,CancellationToken token)
    {
        uint cmd=config.TuyaVersion=="3.3"?7u:13u;
        object body=config.TuyaVersion=="3.3"?new{devId=config.DeviceId,uid=config.DeviceId,t=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),dps}:
            new{protocol=5,t=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),data=new{dps}};
        byte[] plain=JsonPayload(cmd,body);
        await Exchange(cmd,plain,token);
        await Task.Delay(80,token);
    }
    public static string ColorValue(Rgb c,bool legacy)
    {
        double r=c.R/255d,g=c.G/255d,b=c.B/255d,max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),d=max-min;
        double hue=d==0?0:max==r?(g-b)/d%6:max==g?(b-r)/d+2:(r-g)/d+4;if(hue<0)hue+=6;
        int h=(int)Math.Round(hue*60),s=(int)Math.Round((max==0?0:d/max)*(legacy?255:1000)),v=(int)Math.Round(max*(legacy?255:1000));
        return legacy?$"{c.R:x2}{c.G:x2}{c.B:x2}{h:x4}{s:x2}{v:x2}":$"{h:x4}{s:x4}{v:x4}";
    }
    public Task SetColorAsync(Rgb c,bool persist,CancellationToken token)=>Set(new(){{modeDp.ToString(),"colour"},{colorDp.ToString(),ColorValue(c,colorDp==5)}},token);
    public Task SetPowerAsync(bool on,CancellationToken token)=>Set(new(){{powerDp.ToString(),on}},token);
    public async Task PingAsync(CancellationToken token){await Exchange(9,Array.Empty<byte>(),token);}
    public ValueTask DisposeAsync(){client?.Dispose();client=null;return ValueTask.CompletedTask;}
}
