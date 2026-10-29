# BiliDownloader

一个 Windows 桌面端的 B 站视频下载工具：**粘贴链接 → 解析 → 选画质 → 下载**，下载后的音视频由 FFmpeg 合并成单个 MP4。

界面是 WPF 自绘（无边框窗口 + Win11 圆角），支持浅色 / 深色双主题和 5 套预制主题色；Core 层与 UI 层完全解耦，Core 不依赖任何 WPF 类型，可以被命令行或服务端复用。

> v1 定位：单视频 / 多 P 选集的离线备份。付费（充电专属）内容与合集（ugc_season）**不支持下载**，解析时会明确提示。

---

## 功能

| 能力 | 说明 |
| --- | --- |
| 链接解析 | 支持 BV 号、av 号、`b23.tv` 短链，自动识别链接里带的 `?p=N` 并默认选中该集 |
| 多 P 选集 | 预览卡只显示当前选中的一集，点「选择更多」弹出限高弹框勾选（全选 / 反选 / 确认） |
| 画质选择 | 按 `(qn, codecid)` 二元组列出 AVC / HEVC / AV1；没权限的档位保留在列表里并标注原因（需登录 / 大会员），而不是直接隐藏 |
| 音质选择 | 64K / 132K / 192K / 杜比全景声，同样区分可用与否 |
| 扫码登录 | 二维码 180 秒有效，轮询 → 已扫码 → 成功；凭证用 DPAPI 加密存本地，下次启动自动恢复登录态 |
| Cookie 登录 | 扫码不可用时的兜底路径，粘贴 `SESSDATA` 即可 |
| 断点续传 | 分片并发下载，暂停后再继续会自动重取直链续传；取消会清掉断点 |
| 并发与重试 | 任务级并发 + 单任务分片并发，失败自动重试并可回落备用直链端点 |
| 主题与主题色 | 浅色 / 深色即时切换；5 套预制主题色（樱花粉 / 天空蓝 / 薄荷绿 / 紫罗兰 / 琥珀橙），按钮 / 选中项 / 进度条 / 渐变全部跟着变 |
| 设置持久化 | 改动即时写入 `settings.json`，没有「确定」按钮 |

登录后会自动重新解析当前链接，**画质与音质下拉当场刷新**，不需要手动再点一次解析。

---

## 技术栈

- **运行时**：.NET 10（`net10.0-windows` / `net10.0`）
- **UI**：WPF + CommunityToolkit.Mvvm（`[ObservableProperty]` / `[RelayCommand]` 源生成器）
- **DI**：Microsoft.Extensions.DependencyInjection
- **二维码**：QRCoder
- **合并**：外部 FFmpeg（`tools/ffmpeg/ffmpeg.exe`）
- **测试**：xUnit + coverlet
- **加密**：Windows DPAPI（凭证存储）

UI 层所有颜色都走 `Themes/Tokens.*.xaml` 的语义令牌、模板里全部 `DynamicResource`，所以换主题不需要重建界面。

---

## 目录结构

```
src/
  BiliDownloader.Core/          领域层，不依赖 WPF
    Auth/                       扫码登录、Cookie 登录、DPAPI 凭证存储
    BiliApi/                    view / playurl 接口、WBI 签名、画质目录
    Download/                   分片并发下载器
    Merge/                      FFmpeg 合并
    FileName/                   文件名策略（非法字符清理）
    Orchestration/              下载编排：队列、并发、暂停/继续/重试
    Parse/                      URL 解析（BV / av / 短链）
    Storage/                    默认路径
    Utils/                      HttpClient 构造、Cookie 解析、重试
  BiliDownloader.UI/            表现层
    ViewModels/                 MainViewModel / PreviewViewModel / SettingsViewModel / TaskItemViewModel
    Services/                   AppSettings、ThemeService、AccentPalette、ImageCache、QR 渲染
    Themes/                     令牌（浅/深）+ 组件模板 + 图标 Geometry
    DiagnosticsShot.cs          开发期界面取证（--shot=... 输出 PNG + 像素统计）
  BiliDownloader.Tests/         xUnit 单元测试
tools/
  ffmpeg/                       放 ffmpeg.exe / ffprobe.exe（二进制不入库）
  IconForge/                    图标生成与验证工具
方案设计.md                      需求、架构、错误处理、数据设计
视觉规范.md                      配色、间距、圆角、组件行为
```

---

## 环境要求

- Windows 10 1809 及以上
- .NET 10 SDK
- FFmpeg（LGPL 构建即可），放到 `tools/ffmpeg/ffmpeg.exe`；找不到时启动日志会给出提示，无法合并音视频

> 仓库里的 `nuget.config` 把 NuGet 包目录重定向到 `E:\environment\NET\LocalStorage`，如果你的机器上没有这个目录，改掉 `nuget.config` 里的 `globalPackagesFolder` / `http_cache_directory` 再用，或者直接删掉这两个配置项走系统默认缓存。

---

## 构建与运行

```bash
dotnet restore src/BiliDownloader.slnx
dotnet build src/BiliDownloader.slnx -c Release
dotnet run --project src/BiliDownloader.UI/BiliDownloader.UI.csproj
```

单元测试：

```bash
dotnet test src/BiliDownloader.slnx
```

装 .NET Desktop Runtime 的机器上可以直接跑产物：

```
src/BiliDownloader.UI/bin/Release/net10.0-windows/BiliDownloader.UI.exe
```

### 开发期取证参数

不方便肉眼观察界面时（远程桌面 / CI），可以用 `DiagnosticsShot` 把窗口渲染成 PNG：

```bash
BiliDownloader.UI.exe --shot=<输出目录> [--fake=<分P数>] [--fakep=<选第几集>] \
                      [--pages] [--settings] [--accent=<主题色Key>] [--download] [--cancel]
```

`--fake` 会合成一份多 P 元数据直接塞给预览视图模型，**不联网**就能验证选集、画质下拉等布局；输出目录里会额外生成 `report.txt`，包含窗口尺寸、取值范围、各元素文案与像素直方图。

---

## 设置

设置存在 `%APPDATA%\BiliDownloader\settings.json`，主要项：

| 项 | 默认值 | 说明 |
| --- | --- | --- |
| `SaveDirectory` | 我的视频 | 下载保存目录 |
| `MaxConcurrentTasks` | 3 | 同时下载的任务数（1–8） |
| `ConnectionsPerTask` | 4 | 单任务分片并发数（1–16） |
| `PreferredQn` | 80（1080P） | 默认画质 |
| `PreferredCodec` | AVC | 默认编码，可改 HEVC / AV1 |
| `PreferredAudioId` | 30280（192K） | 默认音质 |
| `Theme` | Light | Light / Dark |
| `AccentColor` | Pink | 5 套预制主题色之一 |
| `ExistingFileAction` | Rename | 同名文件改名的处理策略 |

> 并发数和连接数调高确实更快，但也更容易触发风控。真被限流了先把 `ConnectionsPerTask` 调到 1–2。

---

## 已知限制

- **v1 不支持**充电专属 / 付费内容，以及合集（ugc_season）——解析时会明确提示，不会静默失败。
- 没有实现弹幕 / 字幕 / 封面下载。
- 下载临时文件写在保存目录下的 `.bilitmp/`，异常退出时可能残留，手动删除即可。

---

## 免责声明

本项目仅供学习与个人离线备份研究，**不提供任何音视频资源**。下载内容的著作权归原作者与平台所有，请遵守平台的用户协议与当地法律，下载后请在合理期限内自行删除，不要传播或用于商业用途。仓库未指定开源许可证，默认保留所有权利。
