using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace AccountingSystem.Data.Schema;

/// <summary>
/// نگه‌داری تنظیمات اتصال خارج از پوشه‌ی نصب:
/// در Windows رمز عبور با DPAPI کاربر جاری محافظت می‌شود؛ در Unix دسترسی پوشه/فایل به کاربر جاری محدود است.
/// </summary>
public static class SqlConnectionProfileStore
{
    private const string FileName = "sql-connection.bin";
    private const uint CryptProtectUiForbidden = 0x1;

    private static string StorePath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            }

            return Path.Combine(root, "AccountingSystem", FileName);
        }
    }

    public static SqlConnectionProfile? Load()
    {
        var path = StorePath;
        if (!File.Exists(path))
        {
            return null;
        }

        var stored = File.ReadAllBytes(path);
        var clear = OperatingSystem.IsWindows() ? Unprotect(stored) : stored;
        return JsonSerializer.Deserialize<SqlConnectionProfile>(clear);
    }

    public static void Save(SqlConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var path = StorePath;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);

        var clear = JsonSerializer.SerializeToUtf8Bytes(profile);
        var stored = OperatingSystem.IsWindows() ? Protect(clear) : clear;
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, stored);
        RestrictFile(temporary);
        File.Move(temporary, path, overwrite: true);
        RestrictFile(path);
    }

    public static void Delete()
    {
        var path = StorePath;
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] clear)
    {
        var input = ToBlob(clear);
        try
        {
            if (!CryptProtectData(ref input, "AccountingSystem SQL connection", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "محافظت از رمز اتصال SQL با DPAPI انجام نشد.");
            }

            return ReadAndFree(output);
        }
        finally
        {
            FreeInput(input);
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] stored)
    {
        var input = ToBlob(stored);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "خواندن رمز اتصال ذخیره‌شده با DPAPI ممکن نشد.");
            }

            return ReadAndFree(output);
        }
        finally
        {
            FreeInput(input);
        }
    }

    private static DataBlob ToBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Length = bytes.Length, Data = pointer };
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ReadAndFree(DataBlob blob)
    {
        try
        {
            var bytes = new byte[blob.Length];
            Marshal.Copy(blob.Data, bytes, 0, blob.Length);
            return bytes;
        }
        finally
        {
            if (blob.Data != IntPtr.Zero)
            {
                LocalFree(blob.Data);
            }
        }
    }

    private static void FreeInput(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [SupportedOSPlatform("windows")]
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
