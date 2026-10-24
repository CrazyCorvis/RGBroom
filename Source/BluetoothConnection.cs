using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace MagicHomeNET;
public sealed class BluetoothConnection : ILightConnection
{
    readonly ulong address;
    readonly ProtocolKind kind;
    BluetoothLEDevice? device;
    GattDeviceService? service;
    GattCharacteristic? write;
    bool elk13;
    readonly bool random;
    readonly bool preferElk13;
    public string Description=>kind==ProtocolKind.Triones?"Triones · Bluetooth LE":"ELK-BLEDOM · Bluetooth LE";
    public static bool IsValidAddress(string value) => TryAddress(value, out _);
    static Guid Uuid(string shortId)=>Guid.Parse("0000"+shortId+"-0000-1000-8000-00805f9b34fb");
    public static string FormatAddress(ulong value)=>string.Join(":",Enumerable.Range(0,6).Select(i=>((value>>((5-i)*8))&255).ToString("X2")));
    public static bool TryAddress(string text,out ulong value)
    {
        text=text.Replace(":","").Replace("-","").Trim();
        return text.Length==12&&ulong.TryParse(text,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out value) ? true : Fail(out value);
        static bool Fail(out ulong v){v=0;return false;}
    }
    public BluetoothConnection(string mac,ProtocolKind kind,bool random=false,bool elk13Override=false){if(!TryAddress(mac,out address))throw new ArgumentException("Введите Bluetooth MAC: AA:BB:CC:DD:EE:FF");this.kind=kind;this.random=random;preferElk13=elk13Override;}
    public async Task ConnectAsync(CancellationToken token)
    {
        await DisposeAsync();
        // Windows may request access; start device creation on the UI dispatcher.
        var pending=await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=>BluetoothLEDevice.FromBluetoothAddressAsync(address,random?BluetoothAddressType.Random:BluetoothAddressType.Public));
        device=await pending.AsTask(token).WaitAsync(TimeSpan.FromSeconds(15),token);
        if(device==null)throw new IOException("Bluetooth-устройство недоступно. Выполните поиск BLE и проверьте адаптер.");
        Guid serviceId=Uuid(kind==ProtocolKind.Triones?"ffd5":"fff0");
        var services=await device.GetGattServicesForUuidAsync(serviceId,BluetoothCacheMode.Uncached).AsTask(token).WaitAsync(TimeSpan.FromSeconds(15),token);
        if(services.Status!=GattCommunicationStatus.Success||services.Services.Count==0)throw new IOException("Нет подходящего GATT-сервиса. Проверьте семейство BLE-контроллера.");
        service=services.Services[0];foreach(var extra in services.Services.Skip(1))extra.Dispose();
        var chars=await service.GetCharacteristicsForUuidAsync(Uuid(kind==ProtocolKind.Triones?"ffd9":"fff3"),BluetoothCacheMode.Uncached).AsTask(token).WaitAsync(TimeSpan.FromSeconds(15),token);
        if(chars.Status!=GattCommunicationStatus.Success||chars.Characteristics.Count==0)throw new IOException("Нет характеристики записи BLE.");
        write=chars.Characteristics[0];elk13=preferElk13||write.AttributeHandle==13;
        if((write.CharacteristicProperties&(GattCharacteristicProperties.Write|GattCharacteristicProperties.WriteWithoutResponse))==0)throw new IOException("Характеристика BLE не поддерживает запись.");
        if(kind==ProtocolKind.ElkBledom)await Send(elk13?new byte[]{0x7e,4,1,100,1,255,2,1,0xef}:new byte[]{0x7e,0,1,100,0,0,0,0,0xef},token);
    }
    public static byte[] ColorPacket(ProtocolKind kind,Rgb c,bool handle13=false)=>kind==ProtocolKind.Triones?
        new byte[]{0x56,c.R,c.G,c.B,0,0xf0,0xaa}:new byte[]{0x7e,handle13?(byte)7:(byte)0,5,3,c.R,c.G,c.B,handle13?(byte)10:(byte)0,0xef};
    public static byte[] PowerPacket(ProtocolKind kind,bool on,bool handle13=false)=>kind==ProtocolKind.Triones?
        new byte[]{0xcc,on?(byte)0x23:(byte)0x24,0x33}:new byte[]{0x7e,handle13?(byte)4:(byte)0,4,on?(byte)0xf0:(byte)0,0,on?(byte)1:(byte)0,0xff,0,0xef};
    async Task Send(byte[] bytes,CancellationToken token)
    {
        if(write==null||device?.ConnectionStatus!=BluetoothConnectionStatus.Connected)throw new IOException("BLE-соединение потеряно.");
        using var writer=new DataWriter();writer.WriteBytes(bytes);
        var option=(write.CharacteristicProperties&GattCharacteristicProperties.WriteWithoutResponse)!=0?GattWriteOption.WriteWithoutResponse:GattWriteOption.WriteWithResponse;
        var result=await write.WriteValueWithResultAsync(writer.DetachBuffer(),option).AsTask(token).WaitAsync(TimeSpan.FromSeconds(8),token);
        if(result.Status!=GattCommunicationStatus.Success)throw new IOException("Ошибка записи BLE: "+result.Status);
        // BLE controllers generally cannot accept the full Wi-Fi frame rate.
        await Task.Delay(35,token);
    }
    public Task SetColorAsync(Rgb c,bool persist,CancellationToken token)=>Send(ColorPacket(kind,c,elk13),token);
    public Task SetPowerAsync(bool on,CancellationToken token)=>Send(PowerPacket(kind,on,elk13),token);
    public async Task PingAsync(CancellationToken token)
    {
        if(device==null||device.ConnectionStatus!=BluetoothConnectionStatus.Connected)throw new IOException("BLE-контроллер отключён.");
        var result=await device.GetGattServicesForUuidAsync(Uuid(kind==ProtocolKind.Triones?"ffd5":"fff0"),BluetoothCacheMode.Uncached).AsTask(token).WaitAsync(TimeSpan.FromSeconds(12),token);
        foreach(var s in result.Services)s.Dispose();
        if(result.Status!=GattCommunicationStatus.Success||result.Services.Count==0)throw new IOException("BLE-контроллер не ответил на проверку связи.");
    }
    public ValueTask DisposeAsync(){write=null;service?.Dispose();service=null;device?.Dispose();device=null;return ValueTask.CompletedTask;}
    public static async Task<DeviceEntry[]> ScanAsync(CancellationToken token)
    {
        var adapter=await BluetoothAdapter.GetDefaultAsync().AsTask(token);
        if(adapter==null||!adapter.IsLowEnergySupported)throw new IOException("Bluetooth LE адаптер не найден.");
        var found=new ConcurrentDictionary<ulong,DeviceEntry>();
        var watcher=new BluetoothLEAdvertisementWatcher{ScanningMode=BluetoothLEScanningMode.Active};
        string? error=null;
        watcher.Received+=(_,e)=>
        {
            string name=e.Advertisement.LocalName??"";
            bool elk=name.StartsWith("ELK-BLEDOM",StringComparison.OrdinalIgnoreCase)||name.StartsWith("ELK-BLEDDM",StringComparison.OrdinalIgnoreCase)||name.Equals("BLEDOM",StringComparison.OrdinalIgnoreCase)||name.StartsWith("ELK-BLE_",StringComparison.OrdinalIgnoreCase);
            bool triones=name.Contains("Triones",StringComparison.OrdinalIgnoreCase)||name.Contains("LEDBLE",StringComparison.OrdinalIgnoreCase)||name.Contains("LED BLE",StringComparison.OrdinalIgnoreCase);
            if(!elk&&!triones)return;
            string mac=FormatAddress(e.BluetoothAddress);
            found[e.BluetoothAddress]=new(){Ip=mac,Name=name,Protocol=elk?ProtocolKind.ElkBledom:ProtocolKind.Triones,BluetoothRandom=e.BluetoothAddressType==BluetoothAddressType.Random};
        };
        watcher.Stopped+=(_,e)=>{if(e.Error!=BluetoothError.Success)error=e.Error.ToString();};
        try{watcher.Start();await Task.Delay(8000,token);}finally{watcher.Stop();}
        if(error!=null)throw new IOException("Поиск BLE: "+error+". Включите Bluetooth в Windows.");
        return found.Values.OrderBy(d=>d.Name).ToArray();
    }
}
