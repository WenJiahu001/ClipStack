using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ClipStack.Core;

public sealed class UserProtection : IDisposable
{
    private readonly byte[] key;
    public UserProtection(AppPaths paths)
    {
        if (File.Exists(paths.Key)) key = Unprotect(File.ReadAllBytes(paths.Key));
        else
        {
            key = RandomNumberGenerator.GetBytes(32);
            AppPaths.AtomicWrite(paths.Key, Protect(key));
        }
        if (key.Length != 32) throw new CryptographicException("内容指纹密钥不可用。");
    }

    public string Fingerprint(CapturedClip clip)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hash.AppendData(new[] { (byte)clip.Kind });
        hash.AppendData(clip.Content);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static byte[] Protect(byte[] data) => Transform(data, true);
    public static byte[] Unprotect(byte[] data) => Transform(data, false);

    private static byte[] Transform(byte[] data, bool encrypt)
    {
        IntPtr pointer = Marshal.AllocHGlobal(Math.Max(1, data.Length));
        var input = new DataBlob { Length = data.Length, Data = pointer };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(data, 0, pointer, data.Length);
            bool ok = encrypt
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows 数据保护操作失败。", new Win32Exception(Marshal.GetLastWin32Error()));
            byte[] result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            Marshal.Copy(new byte[data.Length], 0, pointer, data.Length);
            Marshal.FreeHGlobal(pointer);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(key);
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
