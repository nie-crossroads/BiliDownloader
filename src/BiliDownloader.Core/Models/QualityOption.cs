namespace BiliDownloader.Core.Models;

/// <summary>
/// 画质选项：必须是 (qn, codecid) 二元组（方案设计 §8.2）。
/// 无法获取的档位保留在列表里并 <see cref="IsAvailable"/>=false，附原因——
/// 用户需要知道「不是没有，是没权限」。
/// </summary>
public sealed record QualityOption(
    int Qn,
    int CodecId,
    string Label,
    string ShortLabel,
    bool IsAvailable,
    string? UnavailableReason,
    int Width,
    int Height,
    long Bandwidth)
{
    public string Display => IsAvailable
        ? Label
        : $"{Label}（{UnavailableReason}）";
}

/// <summary>音质选项（audio id）。</summary>
public sealed record AudioOption(
    int Id,
    string Label,
    bool IsAvailable,
    string? UnavailableReason,
    long Bandwidth)
{
    public string Display => IsAvailable ? Label : $"{Label}（{UnavailableReason}）";
}
