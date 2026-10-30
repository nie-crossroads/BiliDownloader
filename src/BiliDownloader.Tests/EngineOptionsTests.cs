using System.Diagnostics;
using BiliDownloader.Core.Download;
using BiliDownloader.Core.FileName;
using BiliDownloader.Core.Storage;

namespace BiliDownloader.Tests;

/// <summary>
/// 「设置 → 引擎」这条链路的回归测试：这些项以前只是存进 settings.json，引擎根本不读。
/// </summary>
public sealed class EngineOptionsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "BiliDownloaderTests", Guid.NewGuid().ToString("N"));

    // ---------------------------------------------------------------- 全局限速

    [Fact]
    public async Task SpeedLimiter_ZeroRate_NeverWaits()
    {
        var limiter = new TokenBucketSpeedLimiter(0);
        var clock = Stopwatch.StartNew();

        for (var i = 0; i < 20; i++) await limiter.ConsumeAsync(1 << 20);

        Assert.True(clock.ElapsedMilliseconds < 500, $"不限速时不该阻塞，实际耗时 {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task SpeedLimiter_DelaysWhenOverBudget()
    {
        // 1 MB/s 的桶，一次要 1.5 MB：超支 0.5 MB，至少要等约 0.5 秒
        var limiter = new TokenBucketSpeedLimiter(1_000_000);
        var clock = Stopwatch.StartNew();

        await limiter.ConsumeAsync(1_500_000);

        var elapsed = clock.ElapsedMilliseconds;
        Assert.True(elapsed >= 300, $"限速没有生效，只等了 {elapsed} ms");
    }

    [Fact]
    public void SpeedLimiter_UpdateTakesEffectImmediately() =>
        AssertRate(new TokenBucketSpeedLimiter(1000), 1000);

    [Fact]
    public void SpeedLimiter_NegativeRate_ClampsToUnlimited() =>
        AssertRate(new TokenBucketSpeedLimiter(-5), 0);

    private static void AssertRate(ISpeedLimiter limiter, long expected)
    {
        Assert.Equal(expected, limiter.BytesPerSecond);
        limiter.Update(2048);
        Assert.Equal(2048, limiter.BytesPerSecond);
    }

    // ---------------------------------------------------------------- 同名文件策略

    [Fact]
    public async Task Commit_Skip_LeavesExistingFileUntouched()
    {
        Directory.CreateDirectory(_directory);
        var existing = Path.Combine(_directory, "episode.mp4");
        await File.WriteAllTextAsync(existing, "original");
        var source = await CreateSourceAsync("new");

        var result = StoragePaths.Commit(source, _directory, "episode", ".mp4", ExistingFileAction.Skip);

        Assert.Null(result);
        Assert.Equal("original", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task Commit_Overwrite_ReplacesExistingFile()
    {
        Directory.CreateDirectory(_directory);
        var existing = Path.Combine(_directory, "episode.mp4");
        await File.WriteAllTextAsync(existing, "original");
        var source = await CreateSourceAsync("new");

        var result = StoragePaths.Commit(source, _directory, "episode", ".mp4", ExistingFileAction.Overwrite);

        Assert.Equal(existing, result);
        Assert.Equal("new", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task Commit_Rename_KeepsBothFiles()
    {
        Directory.CreateDirectory(_directory);
        var existing = Path.Combine(_directory, "episode.mp4");
        await File.WriteAllTextAsync(existing, "original");
        var source = await CreateSourceAsync("new");

        var result = StoragePaths.Commit(source, _directory, "episode", ".mp4", ExistingFileAction.Rename);

        Assert.NotNull(result);
        Assert.NotEqual(existing, result);
        Assert.Equal("new", await File.ReadAllTextAsync(result!));
        Assert.Equal("original", await File.ReadAllTextAsync(existing));
    }

    // ---------------------------------------------------------------- 命名规则与序号位数

    [Fact]
    public void FileName_HonorsRuleAndPadding()
    {
        var strategy = new DefaultFileNameStrategy();

        Assert.Equal("01 围棋的规则", strategy.Build(1, "围棋的规则", "UP主",
            new FileNameOptions(FileNameRule.IndexAndPart, 2)));
        Assert.Equal("001 围棋的规则", strategy.Build(1, "围棋的规则", "UP主",
            new FileNameOptions(FileNameRule.IndexAndPart, 3)));
        Assert.Equal("围棋的规则", strategy.Build(1, "围棋的规则", "UP主",
            new FileNameOptions(FileNameRule.PlainTitle, 2)));
        Assert.Equal("UP主 - 围棋的规则", strategy.Build(1, "围棋的规则", "UP主",
            new FileNameOptions(FileNameRule.WithUpName, 2)));
    }

    [Fact]
    public void FileName_WithoutOptions_FallsBackToStrategyDefaults()
    {
        // 校验「不传选项」时仍按策略自身构造参数走，老调用点不会突然变样
        Assert.Equal("UP主 - 围棋的规则", new DefaultFileNameStrategy(FileNameRule.WithUpName, 3)
            .Build(1, "围棋的规则", "UP主"));
        Assert.Equal("001 围棋的规则", new DefaultFileNameStrategy(FileNameRule.IndexAndPart, 3)
            .Build(1, "围棋的规则", "UP主"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(9)] // 非 2/3 的值统一按 2 处理，与设置页 Normalize 一致
    public void FileName_Padding_NormalizesToTwoOrThree(int padding) =>
        Assert.Equal(padding == 3 ? 3 : 2, new FileNameOptions(FileNameRule.IndexAndPart, padding).Padding);

    // ---------------------------------------------------------------- 分片落盘

    [Fact]
    public void PartialFile_BytesOnDisk_CountsParts()
    {
        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, "video.m4s");
        File.WriteAllText(target, "12345"); // 主文件 5 字节

        // 没有计划时只看主文件
        Assert.Equal(5, PartialFile.BytesOnDisk(target));

        PartialFile.WritePlan(target, new ChunkPlan(5, 25, 2, 10));
        File.WriteAllText(PartialFile.PartPath(target, 0), "1234567890"); // 10 字节
        File.WriteAllText(PartialFile.PartPath(target, 1), "123");        // 3 字节

        Assert.Equal(5 + 10 + 3, PartialFile.BytesOnDisk(target));
    }

    [Fact]
    public void PartialFile_OversizedPart_IsClampedToPlannedLength()
    {
        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, "video.m4s");

        PartialFile.WritePlan(target, new ChunkPlan(0, 10, 1, 10));
        File.WriteAllText(PartialFile.PartPath(target, 0), "0123456789EXTRA"); // 比计划长

        // 多出来的字节不该被算进进度，否则进度会超过 100%
        Assert.Equal(10, PartialFile.BytesOnDisk(target));
    }

    private async Task<string> CreateSourceAsync(string content)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"source-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
