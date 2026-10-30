using BiliDownloader.Core.Errors;
using BiliDownloader.Core.FileName;
using BiliDownloader.Core.Storage;

namespace BiliDownloader.Tests;

public sealed class StorageAndErrorTests
{
    [Theory]
    [InlineData("...", "未命名")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1", "_COM1")]
    [InlineData("a/b. ", "a_b")]
    public void FileNames_AreUsableOnWindows(string input, string expected) =>
        Assert.Equal(expected, DefaultFileNameStrategy.Sanitize(input));

    [Theory]
    [InlineData(unchecked((int)0x80070070))]
    [InlineData(unchecked((int)0x80070027))]
    public void DiskFull_RecognizesWin32HResult(int hresult) =>
        Assert.Equal(ErrorKind.DiskFull, BiliException.From(new IOException("disk full", hresult)).Kind);

    [Fact]
    public void LongPath_HasSpecificError() =>
        Assert.Equal(ErrorKind.PathTooLong, BiliException.From(new PathTooLongException()).Kind);

    [Fact]
    public async Task ConcurrentCommits_PreserveEveryFileAndExistingTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "BiliDownloaderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var existing = Path.Combine(root, "episode.mp4");
            await File.WriteAllTextAsync(existing, "original");
            var sources = Enumerable.Range(0, 16).Select(i => Path.Combine(root, $"source{i}")).ToArray();
            for (var i = 0; i < sources.Length; i++) await File.WriteAllTextAsync(sources[i], i.ToString());
            var results = await Task.WhenAll(sources.Select(source => Task.Run(() => StoragePaths.Commit(source, root, "episode"))));
            Assert.Equal(sources.Length, results.Distinct().Count());
            Assert.Equal("original", await File.ReadAllTextAsync(existing));
            for (var i = 0; i < results.Length; i++) Assert.Equal(i.ToString(), await File.ReadAllTextAsync(results[i]));
            Assert.Empty(Directory.GetFiles(root, "*.incomplete"));
        }
        finally { Directory.Delete(root, true); }
    }
}
