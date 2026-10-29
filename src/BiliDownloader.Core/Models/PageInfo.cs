namespace BiliDownloader.Core.Models;

/// <summary>分 P / 选集信息（来自 view.data.pages[]）。<see cref="Index"/> 为 1-based。</summary>
public sealed record PageInfo(long Cid, string Part, int Index, TimeSpan Duration);
