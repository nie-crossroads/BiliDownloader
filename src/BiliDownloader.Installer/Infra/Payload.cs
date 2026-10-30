using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace BiliDownloader.Installer.Infra;

/// <summary>
/// 从程序集嵌入资源中提取安装载荷（主程序目录 + FFmpeg + 许可 + 卸载器）。
///
/// 资源在构建时已由 scripts\pack-payload.ps1 预处理，两种格式：
///   主程序目录（net48 多文件）-> zip，Entry.RelPath 为空表示解到安装目录根；
///   其余单文件 -> [8 字节小端 int64 = 原始长度][Deflate 原始流]。
/// net48 没有 SingleFile 的 EnableCompressionInSingleFile，不预压会原样进 exe。
/// </summary>
public sealed class Payload
{
    public sealed class Entry
    {
        public Entry(string resName, string relPath)
        {
            ResName = resName;
            RelPath = relPath;
        }

        /// <summary>嵌入资源名（LogicalName）。</summary>
        public string ResName { get; }

        /// <summary>落地到安装目录的相对路径。</summary>
        public string RelPath { get; }
    }

    public static readonly Entry[] Files =
    {
        // 主程序迁回 net48 后没有 PublishSingleFile，产物是 19 个文件的目录，
        // 整体打成 zip 嵌入。RelPath 为空 = 「解压到安装目录根」，不落到单个文件。
        new Entry("payload.app", ""),
        new Entry("payload.license", Path.Combine("licenses", "ffmpeg-LGPL.txt")),
        new Entry("payload.ffmpeg", Path.Combine("tools", "ffmpeg", "ffmpeg.exe")),
        new Entry("payload.uninstall", "uninstall.exe"),
        new Entry("payload.uninstallcfg", "uninstall.exe.config"),
    };

    /// <summary>载荷解压后的总字节数（只读资源头，不解压）。</summary>
    public static long TotalBytes()
    {
        var asm = Assembly.GetExecutingAssembly();
        long total = 0;
        foreach (var f in Files)
        {
            using (var s = asm.GetManifestResourceStream(f.ResName)
                ?? throw new InvalidOperationException("安装包载荷缺失：" + f.ResName))
            {
                total += f.RelPath.Length == 0 ? ZipUncompressedLength(s) : ReadOriginalLength(s);
            }
        }
        return total;
    }

    /// <summary>zip 载荷的原始总大小：读中央目录里各条目的 Length 之和。</summary>
    private static long ZipUncompressedLength(Stream stream)
    {
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            long total = 0;
            foreach (var entry in zip.Entries)
                total += entry.Length;
            return total;
        }
    }

    /// <summary>提取指定载荷条目到目标目录，返回已写入文件的绝对路径（用于回滚）。</summary>
    public static Task<List<string>> ExtractAsync(
        string targetDir, IEnumerable<Entry> entries, IProgress<long> progress, CancellationToken ct)
    {
        // 解压是 CPU 密集的同步操作，整体丢到线程池，避免卡住 UI 线程的进度动画。
        return Task.Run(() => ExtractCore(targetDir, entries, progress, ct), ct);
    }

    private static List<string> ExtractCore(
        string targetDir, IEnumerable<Entry> entries, IProgress<long> progress, CancellationToken ct)
    {
        var asm = Assembly.GetExecutingAssembly();
        var written = new List<string>();

        foreach (var f in entries)
        {
            ct.ThrowIfCancellationRequested();

            using (var raw = asm.GetManifestResourceStream(f.ResName)
                ?? throw new InvalidOperationException("安装包载荷缺失：" + f.ResName))
            {
                // RelPath 为空 = 目录载荷（zip），解到安装目录根下
                if (f.RelPath.Length == 0)
                {
                    ExtractZip(raw, targetDir, progress, ct, written);
                    continue;
                }

                var outPath = Path.Combine(targetDir, f.RelPath);
                var dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var originalLength = ReadOriginalLength(raw);

                using (var src = new DeflateStream(raw, CompressionMode.Decompress))
                using (var dst = new FileStream(outPath, FileMode.Create, FileAccess.Write,
                           FileShare.None, 1 << 20, FileOptions.None))
                {
                    var buffer = new byte[1 << 20];
                    long copied = 0;
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        dst.Write(buffer, 0, read);
                        copied += read;
                        progress.Report(read);
                        ct.ThrowIfCancellationRequested();
                    }

                    // 长度校验：解压流没有自带长度，出错时宁可失败也不留半截文件。
                    if (copied != originalLength)
                        throw new InvalidDataException(
                            $"载荷 {f.ResName} 解压不完整（{copied}/{originalLength} 字节）");
                }

                written.Add(outPath);
            }
        }

        return written;
    }

    /// <summary>
    /// 解压目录载荷（zip）到 targetDir 根下，逐条目回报进度。
    /// zip 里已做过 deflate，不再套外层压缩。
    /// </summary>
    private static void ExtractZip(
        Stream raw, string targetDir, IProgress<long> progress, CancellationToken ct, List<string> written)
    {
        var root = Path.GetFullPath(targetDir);
        if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            root += Path.DirectorySeparatorChar;

        using (var zip = new ZipArchive(raw, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                // 目录条目以 / 结尾，本身没有内容
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;

                var destPath = Path.GetFullPath(Path.Combine(root, entry.FullName));

                // zip slip 防护：条目路径不得跳出安装目录
                if (!destPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("载荷条目路径非法：" + entry.FullName);

                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using (var src = entry.Open())
                using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write,
                           FileShare.None, 1 << 20, FileOptions.None))
                {
                    var buffer = new byte[1 << 20];
                    long copied = 0;
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        dst.Write(buffer, 0, read);
                        copied += read;
                        progress.Report(read);
                        ct.ThrowIfCancellationRequested();
                    }

                    if (copied != entry.Length)
                        throw new InvalidDataException(
                            $"载荷 {entry.FullName} 解压不完整（{copied}/{entry.Length} 字节）");
                }

                written.Add(destPath);
            }
        }
    }

    private static long ReadOriginalLength(Stream stream)
    {
        var header = new byte[8];
        var read = 0;
        while (read < 8)
        {
            var n = stream.Read(header, read, 8 - read);
            if (n <= 0) throw new InvalidDataException("安装包载荷头损坏");
            read += n;
        }
        return BitConverter.ToInt64(header, 0);
    }
}
