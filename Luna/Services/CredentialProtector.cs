using System.Runtime.InteropServices;
using System.Text;

namespace Luna.Services;

/// <summary>
/// 使用 Windows DPAPI（CryptProtectData / CryptUnprotectData）对敏感字符串进行加解密。
/// 密文绑定当前 Windows 用户，只有同一用户在同一台机器上才能解密。
/// </summary>
public static class CredentialProtector
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        out string? ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>加密明文，返回 Base64 字符串。空输入返回空字符串。</summary>
    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var inBlob = new DATA_BLOB { cbData = plainBytes.Length, pbData = Marshal.AllocHGlobal(plainBytes.Length) };
        try
        {
            Marshal.Copy(plainBytes, 0, inBlob.pbData, plainBytes.Length);
            var empty = new DATA_BLOB();
            if (!CryptProtectData(ref inBlob, "Luna ApiKey", ref empty, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                throw new InvalidOperationException("DPAPI 加密失败，错误码: " + Marshal.GetLastWin32Error());

            try
            {
                var outBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
                return Convert.ToBase64String(outBytes);
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
        }
    }

    /// <summary>解密 Base64 密文，返回明文。空输入或解密失败返回空字符串。</summary>
    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return string.Empty;

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedBase64);
        }
        catch (FormatException)
        {
            return string.Empty;
        }

        var inBlob = new DATA_BLOB { cbData = protectedBytes.Length, pbData = Marshal.AllocHGlobal(protectedBytes.Length) };
        try
        {
            Marshal.Copy(protectedBytes, 0, inBlob.pbData, protectedBytes.Length);
            var empty = new DATA_BLOB();
            if (!CryptUnprotectData(ref inBlob, out _, ref empty, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                return string.Empty;

            try
            {
                var outBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
                return Encoding.UTF8.GetString(outBytes);
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
        }
    }
}
