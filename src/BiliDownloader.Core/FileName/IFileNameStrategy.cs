namespace BiliDownloader.Core.FileName;

/// <summary>命名规则（设置页可选）。</summary>
public enum FileNameRule { IndexAndPart, PlainTitle, WithUpName }

/// <summary>文件名策略抽象（借鉴 downkyicore 的 FileName 模块）。</summary>
public interface IFileNameStrategy
{
    string Build(int? index, string title, string? upName);
}
