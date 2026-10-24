using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

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
