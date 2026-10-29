using BiliDownloader.Core.BiliApi.Models;
using BiliDownloader.Core.Models;

namespace BiliDownloader.Core.BiliApi;

/// <summary>playurl 请求。编码不参与请求（服务端会把所有可用流都返回），仅用于取流时筛选。</summary>
public sealed record PlayUrlRequest(string Bvid, long Cid, int Qn, int CodecId = 0, int Fnval = 0);

/// <summary>B 站 API 客户端抽象。</summary>
public interface IBiliApiClient
{
    /// <summary>登录态与 WBI keys 都来自 nav（未登录也会返回 wbi_img）。</summary>
    Task<NavInfo> GetNavAsync(CancellationToken ct = default);

    /// <summary><paramref name="id"/> 支持 BV 号与 av 号；返回值不含直链。</summary>
    Task<VideoInfo> GetVideoInfoAsync(string id, CancellationToken ct = default);

    /// <summary>取 DASH 直链（含备用 CDN）。</summary>
    Task<PlayUrlResult> GetPlayUrlAsync(PlayUrlRequest request, CancellationToken ct = default);

    /// <summary>当前是否带登录态（决定走 wbi 接口与 fnval 组合）。</summary>
    bool IsLoggedIn { get; }
}
