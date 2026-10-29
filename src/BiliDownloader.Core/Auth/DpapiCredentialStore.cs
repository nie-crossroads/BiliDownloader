using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BiliDownloader.Core.Auth;

/// <summary>
/// 用 Windows DPAPI（CurrentUser 范围）加密凭据后落盘到
/// <c>%LOCALAPPDATA%\BiliDownloader\credential.dat</c>（方案设计 §15）。
/// 非 Windows 平台直接退化为不持久化（返回 null），保证 Core 可跨平台编译与测试。
/// </summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BiliDownloader.Credential.v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public Credential? Current { get; private set; }

    public DpapiCredentialStore(string? filePath = null)
    {
        _path = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BiliDownloader", "credential.dat");
    }

    public async Task<Credential?> LoadAsync(CancellationToken ct = default)
    {
        if (Current is not null) return Current;
        if (!OperatingSystem.IsWindows() || !File.Exists(_path)) return null;

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
            var plain = Unprotect(protectedBytes);
            if (plain is null) return null;

            Current = JsonSerializer.Deserialize<Credential>(plain, JsonOptions);
            return Current;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException)
        {
            // 凭据损坏：当作未登录，不阻断启动
            return null;
        }
    }

    public async Task SaveAsync(Credential credential, CancellationToken ct = default)
    {
        Current = credential;
        if (!OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(credential);
        var protectedBytes = Protect(json);

        // 临时文件 + 替换，避免半写
        var tmp = _path + ".tmp";
        await File.WriteAllBytesAsync(tmp, protectedBytes, ct).ConfigureAwait(false);
        File.Move(tmp, _path, overwrite: true);
    }

    public void Clear()
    {
        Current = null;
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (IOException)
        {
            // 删除失败不影响登出状态
        }
    }

    private static byte[] Protect(byte[] plain) => OperatingSystem.IsWindows()
        ? Dpapi.Protect(plain, Entropy)
        : plain;

    private static byte[]? Unprotect(byte[] cipher)
    {
        if (!OperatingSystem.IsWindows()) return cipher;
        try
        {
            return Dpapi.Unprotect(cipher, Entropy);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>最小 DPAPI 调用（避免额外 NuGet 包；仅 Windows 可用）。</summary>
internal static class Dpapi
{
    public static byte[] Protect(byte[] data, byte[] entropy) => Transform(data, entropy, protect: true);

    public static byte[] Unprotect(byte[] data, byte[] entropy) => Transform(data, entropy, protect: false);

    private static byte[] Transform(byte[] data, byte[] entropy, bool protect)
    {
        var inBlob = ToBlob(data);
        var entropyBlob = ToBlob(entropy);
        DataBlob outBlob = default;

        try
        {
            var ok = protect
                ? CryptProtectData(ref inBlob, "BiliDownloader", ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    0x1 /* CRYPTPROTECT_UI_FORBIDDEN */, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    0x1, ref outBlob);

            if (!ok)
                throw new CryptographicException(Marshal.GetLastWin32Error());

            var result = new byte[outBlob.Size];
            Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
            return result;
        }
        finally
        {
            if (outBlob.Data != IntPtr.Zero) LocalFree(outBlob.Data);
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var blob = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.Data, data.Length);
        return blob;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description,
        ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description,
        ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr ptr);
}
