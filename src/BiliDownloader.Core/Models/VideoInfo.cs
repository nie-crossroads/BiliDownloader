namespace BiliDownloader.Core.Models;

/// <summary>
/// 视频元数据（领域模型，由 view API 映射而来）。
/// <see cref="Qualities"/> / <see cref="Audios"/> 只在 <c>ProbeAsync</c> 时填充，
/// 用于渲染下拉框；两者都不含直链，直链在真正下载时才获取（方案设计 §7.5）。
/// </summary>
public sealed record VideoInfo
{
    public string Bvid { get; init; } = string.Empty;
    public long Avid { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? UpName { get; init; }
    public long UpMid { get; init; }
    public string? CoverUrl { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<PageInfo> Pages { get; init; } = [];
    /// <summary>付费/充电专属等不可下载内容（v1 只提示）。</summary>
    public bool IsPaidContent { get; init; }
    /// <summary>属于合集（ugc_season）：v1 只提示，不展开。</summary>
    public bool HasUgcSeason { get; init; }
    /// <summary>链接里带的 ?p= 选集号（1-based），用于默认选中。</summary>
    public int? SelectedPage { get; init; }

    public IReadOnlyList<QualityOption> Qualities { get; init; } = [];
    public IReadOnlyList<AudioOption> Audios { get; init; } = [];

    public bool IsMultiPage => Pages.Count > 1;
}
