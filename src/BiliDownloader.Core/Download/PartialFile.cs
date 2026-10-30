using System.Globalization;
using System.Text.Json;

namespace BiliDownloader.Core.Download;

/// <summary>
/// 多连接分片的一次切分计划。存成 plan.json 是为了让续传能判断「上一次是按什么边界切的」：
/// 用户改了并发数或片大小，边界就变了，旧分片必须作废，否则拼出来的文件是错位的。
/// </summary>
public sealed record ChunkPlan(long Prefix, long Total, int Count, long ChunkSize)
{
    /// <summary>本次分片负责的字节数（不含主文件里已有的前缀）。</summary>
    public long Span => Total - Prefix;

    /// <summary>每片大小，最后一片可能更短。</summary>
    public long Size => Count <= 0 ? 0 : (Span + Count - 1) / Count;

    public long StartOf(int index) => Prefix + (long)index * Size;

    public long LengthOf(int index) => Math.Max(0, Math.Min(Size, Total - StartOf(index)));

    public bool IsSane => Count > 0 && Total > 0 && Prefix >= 0 && Prefix < Total;
}

/// <summary>
/// 分片下载的落盘布局：主文件 + <c>.partN</c> 分片 + <c>.plan.json</c> 计划。
/// 主文件里的内容始终是「从 0 开始的连续前缀」，分片只是它的尾巴：
/// 这样即使中途把并发数从 4 改成 1，顺序路径按文件长度续传也仍然是安全的。
/// </summary>
public static class PartialFile
{
    private const string PartSuffix = ".part";
    private const string PlanSuffix = ".plan.json";

    public static string PlanPath(string target) => target + PlanSuffix;

    public static string PartPath(string target, int index) =>
        target + PartSuffix + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>计划读不出来 / 内容不合理就当没有，调用方会重新规划。</summary>
    public static ChunkPlan? TryReadPlan(string target)
    {
        try
        {
            var path = PlanPath(target);
            if (!File.Exists(path)) return null;

            var plan = JsonSerializer.Deserialize<ChunkPlan>(File.ReadAllText(path));
            return plan is { IsSane: true } ? plan : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void WritePlan(string target, ChunkPlan plan)
    {
        try
        {
            File.WriteAllText(PlanPath(target), JsonSerializer.Serialize(plan));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 计划写不进去只是续传退化成整片重下，不该让下载失败
        }
    }

    /// <summary>删除所有分片文件（计划变了就调一次，避免旧分片被拼进去）。</summary>
    public static void DeleteParts(string target)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(target));
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

            var prefix = Path.GetFileName(target) + PartSuffix;
            foreach (var file in Directory.EnumerateFiles(directory, prefix + "*"))
                TryDelete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>下载完成后的收尾：分片和计划都不该留下。</summary>
    public static void Cleanup(string target)
    {
        DeleteParts(target);
        TryDelete(PlanPath(target));
    }

    /// <summary>
    /// 已经落盘的字节数（用于进度显示）。拼接阶段每拼完一片就删掉该片，
    /// 所以「主文件长度 + 现存分片长度」在任何时刻都是真实进度。
    /// </summary>
    public static long BytesOnDisk(string target)
    {
        var main = File.Exists(target) ? new FileInfo(target).Length : 0;

        var plan = TryReadPlan(target);
        if (plan is null) return main;

        long sum = 0;
        for (var i = 0; i < plan.Count; i++)
        {
            var part = PartPath(target, i);
            if (File.Exists(part)) sum += Math.Min(new FileInfo(part).Length, plan.LengthOf(i));
        }

        return main + sum;
    }

    /// <summary>删掉一个文件，失败静默（清理类操作不该让下载失败）。</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
