namespace BiliDownloader.Core.Models;

/// <summary>下载进度快照（单个流）。</summary>
public sealed record DownloadProgress(long ReceivedBytes, long TotalBytes, double SpeedBytesPerSecond)
{
    public double Fraction => TotalBytes > 0 ? (double)ReceivedBytes / TotalBytes : 0;
}
