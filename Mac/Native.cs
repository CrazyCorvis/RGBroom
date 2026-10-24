using System.Runtime.InteropServices;
using System.Text.Json;

namespace MagicHomeNET;

internal static class Native
{
    const string Library = "RGBroomNative";
    [DllImport(Library)] static extern IntPtr rgb_request([MarshalAs(UnmanagedType.LPUTF8Str)] string json);
    [DllImport(Library)] static extern void rgb_free(IntPtr value);
    [DllImport(Library)] internal static extern int rgb_audio(int handle, [Out] float[] samples);
    [DllImport(Library)] internal static extern int rgb_screen(int handle, [Out] byte[] pixels);
    public static JsonElement Call(object request)
    {
        var ptr = rgb_request(JsonSerializer.Serialize(request));
        try
        {
            using var doc = JsonDocument.Parse(Marshal.PtrToStringUTF8(ptr) ?? "{}");
            var result = doc.RootElement.Clone();
            if (result.TryGetProperty("error", out var error)) throw new IOException(error.GetString());
            return result;
        }
        finally { rgb_free(ptr); }
    }
}

public static class LocalSecrets
{
    public static string Protect(string value) => "keychain:" + Native.Call(new { op = "secret-save", value }).GetProperty("id").GetString();
    public static string Reveal(string value)
    {
        if (!value.StartsWith("keychain:")) throw new IOException("Ключ Windows DPAPI нельзя перенести на Mac. Введите local_key заново.");
        return Native.Call(new { op = "secret-read", id = value[9..] }).GetProperty("value").GetString()!;
    }
    public static void Delete(string value)
    {
        if (value.StartsWith("keychain:")) Native.Call(new { op = "secret-delete", id = value[9..] });
    }
}
