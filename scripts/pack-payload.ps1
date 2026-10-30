# ============================================================
#  把安装载荷预先压缩，供安装器作为嵌入资源打包。
#
#  两类载荷，两种格式：
#    1) 主程序目录（net48 没有 PublishSingleFile，产物是 19 个文件）
#       -> payload.app.zip，zip 自带压缩，不再套外层 Deflate
#    2) 其余单文件（FFmpeg / 许可 / 卸载器）
#       -> *.def：[8 字节小端 int64 = 原始长度][Deflate 原始流]
#
#  为什么需要预压缩：
#    安装器跑在 .NET Framework 4.8 上（系统自带，无需附带运行时），
#    但 net48 没有 SingleFile 的 EnableCompressionInSingleFile，不压缩会原样进 exe。
# ============================================================
param(
    [string]$Root = ""
)

$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$outDir = Join-Path $Root 'build\payload'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$totalSrc = 0
$totalDst = 0

# ------------------------------------------------------------
#  1) 主程序目录 -> payload.app.zip
# ------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem

$appDir = Join-Path $Root 'dist\publish'
if (-not (Test-Path $appDir)) {
    Write-Host "[错误] 缺少载荷源：dist\publish（先发布主程序）" -ForegroundColor Red
    exit 1
}

$appZip = Join-Path $outDir 'payload.app.zip'
$appFiles = @(Get-ChildItem -Path $appDir -Recurse -File)
$appSrcBytes = ($appFiles | Measure-Object -Property Length -Sum).Sum
$appNewest = ($appFiles | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum

if ((Test-Path $appZip) -and ((Get-Item $appZip).LastWriteTimeUtc -ge $appNewest)) {
    Write-Host "  跳过 payload.app.zip（已是最新）"
} else {
    if (Test-Path $appZip) { Remove-Item $appZip -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $appDir, $appZip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    Write-Host ("  压缩 {0,-24} {1,7:N1} MB -> {2,7:N1} MB（{3} 个文件）" -f
        'payload.app.zip', ($appSrcBytes / 1MB), ((Get-Item $appZip).Length / 1MB), $appFiles.Count)
}

$totalSrc += $appSrcBytes
$totalDst += (Get-Item $appZip).Length

# ------------------------------------------------------------
#  2) 单文件载荷 -> *.def（长度头 + Deflate）
# ------------------------------------------------------------
$map = @(
    @{ Src = 'tools\ffmpeg\ffmpeg.exe';               Name = 'payload.ffmpeg.def' },
    @{ Src = 'licenses\ffmpeg-LGPL.txt';              Name = 'payload.license.def' },
    @{ Src = 'dist\uninstaller\uninstall.exe';        Name = 'payload.uninstall.def' },
    @{ Src = 'dist\uninstaller\uninstall.exe.config'; Name = 'payload.uninstallcfg.def' }
)

foreach ($m in $map) {
    $src = Join-Path $Root $m.Src
    if (-not (Test-Path $src)) {
        Write-Host "[错误] 缺少载荷源：$($m.Src)" -ForegroundColor Red
        exit 1
    }

    $dst = Join-Path $outDir $m.Name

    # 源文件没更新就跳过，避免每次构建都重压。
    if ((Test-Path $dst) -and
        ((Get-Item $dst).LastWriteTimeUtc -ge (Get-Item $src).LastWriteTimeUtc)) {
        $totalSrc += (Get-Item $src).Length
        $totalDst += (Get-Item $dst).Length
        Write-Host ("  跳过 {0}（已是最新）" -f $m.Name)
        continue
    }

    $in = [IO.File]::OpenRead($src)
    $tmp = $dst + '.tmp'
    try {
        $fs = [IO.File]::Create($tmp)
        try {
            # 先写原始长度，解压端据此计算进度、校验完整性。
            $lenBytes = [BitConverter]::GetBytes([Int64]$in.Length)
            $fs.Write($lenBytes, 0, 8)

            $ds = New-Object System.IO.Compression.DeflateStream(
                $fs, [System.IO.Compression.CompressionLevel]::Optimal, $true)
            try {
                $buf = New-Object byte[] (1048576)
                while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) {
                    $ds.Write($buf, 0, $n)
                }
            } finally { $ds.Dispose() }
        } finally { $fs.Dispose() }
    } finally { $in.Dispose() }

    Move-Item -Force $tmp $dst

    $totalSrc += (Get-Item $src).Length
    $totalDst += (Get-Item $dst).Length
    Write-Host ("  压缩 {0,-32} {1,7:N1} MB -> {2,7:N1} MB" -f
        $m.Name, ((Get-Item $src).Length / 1MB), ((Get-Item $dst).Length / 1MB))
}

# 清掉旧格式残留，避免误以为是有效载荷
$stale = Join-Path $outDir 'payload.app.def'
if (Test-Path $stale) { Remove-Item $stale -Force }

Write-Host ("  载荷合计 {0:N1} MB -> {1:N1} MB" -f ($totalSrc / 1MB), ($totalDst / 1MB))
