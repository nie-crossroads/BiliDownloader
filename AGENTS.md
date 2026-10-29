# AGENTS.md — BiliDownloader

本文件为 **AI 编程代理** 提供该仓库的开发指引。人类开发者也应通读。

## 1. 项目简介

Windows 桌面端 **B 站视频下载工具**。用户在链接框粘贴 / 输入 B 站链接后点击解析即可下载；支持**免登录 1080P** 与**登录（QR 扫码）解锁高清 / 大会员画质**。

- 范围：v1 仅做普通 UGC 视频（含分 P / 合集）。番剧 / 课程 / 充电专属留到 v2。
- 产品 / 代码名统一：**BiliDownloader**。
- 交付形态：最终打包为一个 **.exe 安装程序**（Inno Setup，支持选安装路径），`--self-contained` 发布，用户免装 .NET 运行时。

## 2. 技术栈

| 项 | 选型 | 说明 |
|---|---|---|
| 语言 / 框架 | C# + WPF（XAML + MVVM） | 目标框架 `net10.0`(Core) / `net10.0-windows`(UI) |
| .NET SDK | **10.0.403** | 自定义安装于 `E:\environment\NET\NET SDK10`（已加入用户 PATH） |
| MVVM | `CommunityToolkit.Mvvm` 8.4.2 | 源生成器，少样板代码 |
| DI | `Microsoft.Extensions.DependencyInjection` | 在 `App.xaml.cs` 用 `ServiceCollection` 装配 Core 服务（参考 downkyicore 的 Generic Host + DI 思路，WPF 下简化为直接建容器，不引入完整 Generic Host） |
| HTTP | `HttpClient` 单例 + `SocketsHttpHandler` | `Referer=https://www.bilibili.com` + 浏览器 UA |
| 合并 | 内嵌 `FFmpeg` | `Process` 调用，合成 MP4/MKV |
| 打包 | Inno Setup（`.iss`） | `dotnet publish --self-contained` 后打包，向导可改安装路径 |

## 3. 架构（参考 downkyicore / 哔哩下载姬跨平台版）

> 仅**借鉴其模块划分与启动方式**，不复制其代码。downkyicore 是 Avalonia 跨平台版，本项目锁定 Windows + WPF。

**三层分离**：表现层(WPF) / 核心引擎(Core 类库，零 UI 依赖) / 外部依赖(B 站 API、FFmpeg)。

Core 内部模块（对应 downkyicore 的 `BiliApi / Models / Utils / FileName` 划分，自行实现）：

```
BiliDownloader.Core/
├── BiliApi/   接口客户端 + WBI 签名 + API DTO 模型
├── Models/    领域模型（VideoTask / PageInfo / QualityOption / DownloadProgress）
├── Parse/     链接解析（BV/av/选集 p、b23.tv 短链展开）
├── Auth/      登录态（QR 扫码登录、Cookie 持久化）
├── Download/  下载管理（分块 / 断点续传 / 重试 / 并发）
├── Merge/     FFmpeg 合并音视频
├── FileName/  文件名清洗与命名策略（独立模块，借鉴 downkyicore 的 FileName/）
└── Utils/     HttpClient 工厂 / 重试 / 路径等工具
```

**关键设计点（来自参考项目）**：
1. 文件名清洗/命名策略独立成 `FileName` 模块，与下载、合并解耦，便于单测与多规则切换。
2. 用 DI 容器管理依赖，不在业务代码里 `new` 具体实现。
3. 内置下载器思路："先规划分段 → 再并行多连接下载 → 保存严格校验的断点续传状态"（M3 实现）。
4. 诊断日志脱敏（Cookie / token / uid / 本机路径），v1 后补。

## 4. 目录结构

```
B站视频下载工具/
├── nuget.config                     # NuGet 还原路径固化到本地 LocalStorage
├── .gitignore                       # 忽略 bin/obj/publish/LocalStorage/tools 二进制
├── AGENTS.md                        # 本文件
├── 方案设计.md                       # 详细方案（v1.1，含数据契约/磁盘规范/错误映射/DoD）
├── 视觉规范.md                       # 视觉规范「静流」（令牌/字号/控件规格/动效/WPF 落地）
├── design/                          # 三份视觉稿，默认主题均为浅色，右上角可切深色
│   ├── mockup.html                  # 主界面（登录态与扫码、开标注、组件与令牌展示）
│   ├── installer.html               # 安装向导（3 步 + 卸载）
│   └── screens.html                 # 第 2 批：设置/首启自检/大合集选集/错误态/确认/断点恢复/菜单/关于
├── src/
│   ├── BiliDownloader.slnx          # 解决方案（.NET 10 新一代 slnx 格式）
│   ├── Directory.Build.props        # 全局编译属性（CET 兼容兜底）
│   ├── BiliDownloader.UI/           # WPF 表现层 (net10.0-windows)
│   │   ├── App.xaml / App.xaml.cs   # 入口：DI 装配、全局异常钩子、解析 MainViewModel
│   │   ├── MainWindow.xaml(.cs)     # 主窗口：链接框 + 预览 + 任务列表 + 进度/日志
│   │   ├── Views/                   # PreviewView / SettingsView / TaskItemView
│   │   ├── ViewModels/              # MainViewModel / PreviewViewModel / SettingsViewModel / TaskItemViewModel
│   │   ├── Services/                # FfmpegLocator / DialogService / QrImageRenderer
│   │   ├── Converters/              # 状态→颜色、进度→文本、字节→人类可读
│   │   ├── Themes/                  # Tokens.Dark/Light.xaml + Components.xaml + Icons.xaml（见 视觉规范 §10.2）
│   │   └── Resources/               # 图标、样式
│   ├── BiliDownloader.Core/         # 核心引擎（纯类库，禁引用 PresentationFramework）
│   │   ├── Orchestration/           # ★ IVideoDownloadService 流水线编排（解析→取链→下载→合并→落盘）
│   │   ├── BiliApi/                 # IBiliApiClient / BiliApiClient / WbiSign / Endpoints / Models(DTO)
│   │   ├── Parse/                   # UrlParser / 短链展开
│   │   ├── Auth/                    # ICredentialStore / IQrLogin / QrLogin
│   │   ├── Download/                # IFileDownloader(单流分片) / IDownloadQueue(并发调度)
│   │   ├── Storage/                 # ★ TempWorkspace / DownloadManifest / 原子提交
│   │   ├── Merge/                   # IFfmpegMerger / FfmpegMerger（带进度、可取消）
│   │   ├── FileName/                # IFileNameStrategy / DefaultFileNameStrategy
│   │   ├── Models/                  # 领域模型（VideoTask / TaskPageItem / DashStream ...）
│   │   ├── Errors/                  # ★ BiliException 体系 + ErrorKind 分类
│   │   └── Utils/                   # HttpClientFactory / CookieDelegatingHandler / RetryHelper
│   └── BiliDownloader.Tests/         # xUnit（签名 / 解析 / 文件名 / API 映射 / 下载器 / 编排）
├── tools/
│   ├── ffmpeg/                      # 内嵌 ffmpeg.exe + ffprobe.exe（LGPL 构建，二进制不入库）
│   └── IconForge/                   # 图标生成器：spec.json 是唯一事实源，渲染多尺寸 ico（见 §6「重新生成图标」）
└── 方案设计.md                       # 详细方案（v1.1，含数据契约/磁盘规范/错误映射/DoD）
```

## 5. 关键约束与约定（务必遵守）

- **Core 工程不得引用 `PresentationFramework` 等 WPF 程序集**，保证引擎可在控制台 / 测试独立运行。
- **禁止私自提交 / 推送**：未经用户明确同意，不得 `git commit` / `git push` / 建 PR。改动只留工作区，等用户说"提交/推送"再动手（本仓库既有红线）。
- **易变项集中管理 + 单测锁定**：WBI 签名、DASH 直链、清晰度 id 等随 B 站风控变化，逻辑独立成模块并写单测；关注 BBDown / DownKyi 社区更新。
- **命名规则**默认"序号+选集标题"（`01 第1节：围棋的规则.mp4`），合集建同名子目录；支持"纯标题""含 UP 主"两种。
- **清晰度边界**：免登录上限 1080P（实测）；高清 / 大会员需登录态。

## 6. 环境与构建

- **SDK 自定义路径**：`E:\environment\NET\NET SDK10\dotnet.exe`（已入用户 PATH）。命令行需 `export PATH="/e/environment/NET/NET SDK10:$PATH"`（Git Bash）或在 PowerShell 用绝对路径。
- **✅ CET 兼容问题已解决（2026-10-29）**：开发机已升级到 **Windows 11（build 26200）**，.NET 10 Roslyn 不再崩。`Directory.Build.props` 里的 `<EnableCetCompatibility>false>` / `<UseSharedCompilation>false>` 可保留（无害），也可在确认无用后移除。历史背景：Win10 19045 上编译会报 `Your Windows doesn't fully support CET`。
- **⚠️ 直接双击 exe 需要 `DOTNET_ROOT`**：SDK 装在 `E:\environment\NET\NET SDK10`，且**未写入**注册表 `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\InstallLocation`，所以 apphost 找不到运行时会报
  `You must install .NET to run this application ... .NET location: Not found`。
  - 临时运行（Git Bash）：`export DOTNET_ROOT="E:\environment\NET\NET SDK10"` 再执行 exe。
  - 想双击也能跑：`setx DOTNET_ROOT "E:\environment\NET\NET SDK10"`（需重开终端）。
  - **`--self-contained` 发布不受影响**（自带运行时），最终安装包也是这条路。
- **⚠️ 受限 shell 下需补 `ProgramData` / `ProgramFiles`**：在沙箱/精简环境变量里这两个变量可能为空，会分别导致
  NuGet 还原抛 `Value cannot be null. (Parameter 'path1')`（`XPlatMachineWideSetting` 取机器级配置目录失败）、apphost 报 `the default install location cannot be obtained`。
  构建前补上即可：
  ```bash
  export ProgramData="C:\ProgramData"; export ProgramFiles="C:\Program Files"
  ```
- **NuGet 本地化**：所有依赖还原到 `E:\environment\NET\LocalStorage`（见 `nuget.config` 与用户环境变量 `NUGET_PACKAGES` / `NUGET_HTTP_CACHE_PATH`），不占系统盘默认缓存。
- **FFmpeg 未就位也不影响启动**：`IFfmpegMerger` 在 DI 里是惰性工厂，`FfmpegLocator.Find()` 只在首次解析该服务时执行；`tools/ffmpeg/` 为空只会让「合并」这一步报错，不会拖垮启动。跑通完整下载前需把 LGPL 构建的 `ffmpeg.exe`（+`ffprobe.exe`）放进 `tools/ffmpeg/`。
- **构建命令**（在 `src/` 下）：
  ```bash
  dotnet build BiliDownloader.slnx -c Debug
  dotnet publish BiliDownloader.UI -c Release -r win-x64 --self-contained true -o ../publish
  ```
- **运行单测**：`dotnet test BiliDownloader.slnx -c Debug`
- **重新生成图标**：图标**不要手改** `Resources/app.ico`，改 `tools/IconForge/spec.json` 里的几何后重跑（输出直接覆盖到 `src/BiliDownloader.UI/Resources/`）：
  ```bash
  cd tools/IconForge
  dotnet build -c Release
  ./bin/Release/net10.0-windows/IconForge.exe render spec.json   # 出 ico + 窗口用的 256px png
  ./bin/Release/net10.0-windows/IconForge.exe sheet  spec.json png/sheet.png  # 各尺寸对比图 + 逐像素点阵
  ./bin/Release/net10.0-windows/IconForge.exe verify ../../src/BiliDownloader.UI/Resources/app.ico  # 校验 WPF 能读全部帧
  ./bin/Release/net10.0-windows/IconForge.exe exeicon ../../src/BiliDownloader.UI/bin/Debug/net10.0-windows/BiliDownloader.UI.exe  # 校验 exe 内嵌图标
  ```
  **改完必须做三面对照**（任务栏 / 标题栏小标 / 托盘），只验 256px 母版或只验设计稿会漏掉「两个面不是同一个符号」。窗口内容用 `capture` 抓（走 DWM，被遮挡也能抓）：
  ```bash
  ./bin/Release/net10.0-windows/IconForge.exe capture <pid> ../../tools/IconForge/png/app.png
  ./bin/Release/net10.0-windows/IconForge.exe focus <pid> [x y w h]   # 可选：把窗口挪到指定位置并临时置顶
  ```
  图标规范见 `视觉规范.md` §8.1。
- **运行界面**：`dotnet run --project BiliDownloader.UI`（或直接跑 `BiliDownloader.UI\bin\Debug\net10.0-windows\BiliDownloader.UI.exe`，记得设 `DOTNET_ROOT`）

## 7. 开发里程碑

`M0 脚手架` → `M1 链接解析` → `M2 单视频跑通(免登录)` → `M3 下载管理器` → `M4 登录态(高清)` → `M5 体验打磨` → `v2(弹幕/字幕/批量/番剧)`。

当前（2026-10-29）：**M0 已通过**——升级 Win11 后 `dotnet build` **0 错误 0 警告**，`dotnet test` **4/4 通过**，WPF 窗口可正常启动（标题 `BiliDownloader`）。业务代码仍是桩：`UrlParser` 已实现（本地正则 + 单测），`BiliApiClient` / `DownloadManager` / `FfmpegMerger` 抛 `NotImplementedException`；`FfmpegLocator` 找到不找到都不影响启动（惰性求值）。
界面目前是 M0 版（输入框 + 解析/下载按钮 + 任务列表 + 日志），§6.1 的三步式预览区/画质音质选择在 M5 落地。

设计文档已于同日修订为 **v1.1**：补齐编排层 `IVideoDownloadService`、数据契约与状态机、磁盘布局/断点续传/原子提交、清晰度与音质规则、错误分类映射、里程碑 DoD、设置项 schema；并修正了 WBI 过滤字符等 10 处问题（见 `方案设计.md` §18）。

**交互与产品口径已定稿（2026-10-29，详见 `方案设计.md` §16 / §19）**：
- **三步式界面**：输入框粘贴 → 点【解析】→ 预览区选 P / 画质 / 音质 → 点【下载】。**不做剪贴板自动捕获**。
- 单 P 视频也带 `01 ` 序号前缀；v1 做暂停/继续；多 P 中单个 P 失败不中断整批；安装免 UAC（per-user）。

**开工前必读 `方案设计.md` §4.3 / §4.4 / §5 / §6.1 / §7**，接口形状与交互细节以文档为准。

## 8. 法律与合规

仅个人学习 / 使用；不内置公共账号、不绕过付费；遵守 B 站用户协议与合理使用边界。
