using System.Globalization;
namespace MagicHomeNET;

public sealed class BluetoothConnection : ILightConnection
{
    readonly string id;
    readonly ProtocolKind kind;
    readonly bool elk13;
    int handle;
    public string Description => kind + " · CoreBluetooth";
    public static bool IsValidAddress(string value) => Guid.TryParse(value, out _);
    // Kept for shared packet tests; macOS connection identifiers are UUIDs.
    public static string FormatAddress(ulong value) => string.Join(":", Enumerable.Range(0,6).Select(i => ((value >> ((5-i)*8)) & 255).ToString("X2")));
    public static bool TryAddress(string text, out ulong value) { value = 0; text = text.Replace(":", "").Replace("-", ""); return text.Length == 12 && ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value); }
    public BluetoothConnection(string id, ProtocolKind kind, bool random = false, bool elk13Override = false)
    {
        if (!IsValidAddress(id)) throw new ArgumentException("На macOS выберите BLE-устройство через поиск (UUID вместо MAC).");
        this.id = id; this.kind = kind; elk13 = elk13Override;
    }
    public async Task ConnectAsync(CancellationToken token)
    {
        await DisposeAsync();
        handle = await Task.Run(() => Native.Call(new { op = "ble-connect", id, triones = kind == ProtocolKind.Triones }).GetProperty("handle").GetInt32(), token);
        if (token.IsCancellationRequested) { await DisposeAsync(); token.ThrowIfCancellationRequested(); }
        if (kind == ProtocolKind.ElkBledom) await Send(elk13 ? new byte[] {0x7e,4,1,100,1,255,2,1,0xef} : new byte[] {0x7e,0,1,100,0,0,0,0,0xef}, token);
    }
    public static byte[] ColorPacket(ProtocolKind kind,Rgb c,bool handle13=false)=>kind==ProtocolKind.Triones?
        new byte[]{0x56,c.R,c.G,c.B,0,0xf0,0xaa}:new byte[]{0x7e,handle13?(byte)7:(byte)0,5,3,c.R,c.G,c.B,handle13?(byte)10:(byte)0,0xef};
    public static byte[] PowerPacket(ProtocolKind kind,bool on,bool handle13=false)=>kind==ProtocolKind.Triones?
        new byte[]{0xcc,on?(byte)0x23:(byte)0x24,0x33}:new byte[]{0x7e,handle13?(byte)4:(byte)0,4,on?(byte)0xf0:(byte)0,0,on?(byte)1:(byte)0,0xff,0,0xef};
    async Task Send(byte[] bytes, CancellationToken token)
    {
        await Task.Run(() => Native.Call(new { op = "ble-write", handle, bytes = Convert.ToHexString(bytes) }), token);
        await Task.Delay(35, token);
    }
    public Task SetColorAsync(Rgb c, bool persist, CancellationToken token) => Send(ColorPacket(kind,c,elk13), token);
    public Task SetPowerAsync(bool on, CancellationToken token) => Send(PowerPacket(kind,on,elk13), token);
    public Task PingAsync(CancellationToken token) => Task.Run(() => Native.Call(new { op = "ble-ping", handle }), token);
    public ValueTask DisposeAsync() { if (handle != 0) { Native.Call(new { op = "ble-close", handle }); handle = 0; } return ValueTask.CompletedTask; }
    public static async Task<DeviceEntry[]> ScanAsync(CancellationToken token)
    {
        var result = await Task.Run(() => Native.Call(new { op = "ble-scan" }), token);
        return result.GetProperty("devices").EnumerateArray().Select(d => new DeviceEntry {
            Ip = d.GetProperty("id").GetString()!, Name = d.GetProperty("name").GetString()!,
            Protocol = d.GetProperty("triones").GetBoolean() ? ProtocolKind.Triones : ProtocolKind.ElkBledom
        }).ToArray();
    }
}
