namespace BiliDownloader.Core.FileName;

/// <summary>命名规则（设置页可选）。</summary>
public enum FileNameRule { IndexAndPart, PlainTitle, WithUpName }

/// <summary>
/// 命名参数（设置页「命名规则」+「序号位数」）。入队时固定下来传给 Core，
/// 这样下载途中改设置不会影响已经在跑的任务，Core 也不用反向依赖 UI 的 AppSettings。
/// </summary>
public sealed record FileNameOptions(FileNameRule Rule = FileNameRule.IndexAndPart, int IndexPadding = 2)
{
    /// <summary>补零位数只接受 2 / 3，其它值统一按 2 处理，与设置页 Normalize 保持一致。</summary>
    public int Padding => IndexPadding == 3 ? 3 : 2;
}

/// <summary>文件名策略抽象（借鉴 downkyicore 的 FileName 模块）。</summary>
public interface IFileNameStrategy
{
    string Build(int? index, string title, string? upName, FileNameOptions? options = null);
}
