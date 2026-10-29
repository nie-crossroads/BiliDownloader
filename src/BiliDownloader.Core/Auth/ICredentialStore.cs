namespace BiliDownloader.Core.Auth;

/// <summary>登录凭据（Cookie + 展示用账号信息），只保存在本机。</summary>
public sealed record Credential
{
    public string Cookie { get; init; } = string.Empty;
    public long Uid { get; init; }
    public string? Uname { get; init; }
    public string? Face { get; init; }
    public bool IsVip { get; init; }
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.Now;

    public bool HasCookie => !string.IsNullOrWhiteSpace(Cookie);
}

/// <summary>凭据持久化（DPAPI 本地加密，不上传任何服务器）。</summary>
public interface ICredentialStore
{
    /// <summary>当前内存中的凭据（登录后立即生效，供 Cookie 注入使用）。</summary>
    Credential? Current { get; }

    Task<Credential?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(Credential credential, CancellationToken ct = default);

    void Clear();
}
