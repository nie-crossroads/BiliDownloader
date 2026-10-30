namespace BiliDownloader.Core.Storage;

/// <summary>
/// 目标文件已存在时的处理策略（设置项 ExistingFileAction）。
/// Core 只认这个枚举，UI 负责把自己的字符串映射过来。
/// </summary>
public enum ExistingFileAction
{
    /// <summary>自动改名，追加 (1)(2) 后缀，不覆盖任何已有文件。</summary>
    Rename,

    /// <summary>已存在就跳过：不写文件，该分 P 记为 Skipped。</summary>
    Skip,

    /// <summary>直接覆盖已有文件。</summary>
    Overwrite
}
