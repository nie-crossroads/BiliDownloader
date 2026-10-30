# BiliDownloader

一个 Windows 桌面端的 B 站视频下载工具：**粘贴链接 → 解析 → 选画质 → 下载**，下载后的音视频由 FFmpeg 合并成单个 MP4。

界面是 WPF 自绘（无边框窗口 + Win11 圆角），支持浅色 / 深色双主题和 5 套预制主题色；Core 层与 UI 层完全解耦，Core 不依赖任何 WPF 类型，可以被命令行或服务端复用。

> v1 定位：单视频 / 多 P 选集的离线备份。付费（充电专属）内容与合集（ugc_season）**不支持下载**，解析时会明确提示。
> 代码规范、行为红线与提交格式见 [AGENTS.md](./AGENTS.md)。

---

## 功能

| 能力 | 说明 |
| --- | --- |
| 链接解析 | 支持 BV 号、av 号、`b23.tv` 短链，自动识别链接里带的 `?p=N` 并默认选中该集 |
| 多 P 选集 | 预览卡只显示当前选中的一集，点「选择更多」弹出限高弹框勾选（全选 / 反选 / 确认） |
| 画质选择 | 下拉按清晰度列出（1080P 高清 / 4K 超清 …），同一清晰度只出一条；具体用哪路码流由设置里的「默认编码」决定。没权限的档位保留在列表里并标注原因（需登录 / 大会员），而不是直接隐藏 |
| 音质选择 | 64K / 132K / 192K / 杜比全景声，同样区分可用与否 |
| 扫码登录 | 二维码 180 秒有效，轮询 → 已扫码 → 成功；凭证用 DPAPI 加密存本地，下次启动自动恢复登录态 |
| Cookie 登录 | 扫码不可用时的兜底路径，粘贴 `SESSDATA` 即可 |
| 断点续传 | 顺序 Range 分块下载，同一任务暂停后继续会重取直链续传；取消会清掉断点 |
| 跨重启恢复 | 任务清单落在 `.bilitmp/manifest.json`，重启后自动恢复为「已暂停」等用户点继续；续传前校验流指纹，对不上就整段重下 |
| 并发分片 | 设置里「单任务连接数」> 1 时按片并发：每片写独立的 `.partN`，全部完成后拼回主文件。主文件里始终是连续前缀，中途把连接数改回 1 也不会损坏文件 |
| 全局限速 | 共享令牌桶，所有任务与所有分片共用一个预算，设置页改动即时生效 |
| 并发与重试 | 任务级并发（1–8）+ 可选分片并发（1–16）；失败按重试预算处理，备用直链回退可在设置里关掉 |
| 主题与主题色 | 浅色 / 深色即时切换；5 套预制主题色（樱花粉 / 天空蓝 / 薄荷绿 / 紫罗兰 / 琥珀橙），按钮 / 选中项 / 进度条 / 渐变全部跟着变 |
| 设置持久化 | 改动即时写入 `settings.json`，没有「确定」按钮 |
| 托盘常驻 | 设置 → 基础 →「关闭时」选「最小化到任务栏」后，点关闭**不会退出**：窗口隐藏、右下角托盘留图标，双击恢复、右键菜单可真正退出，后台下载继续跑 |

登录后会自动重新解析当前链接，**画质与音质下拉当场刷新**，不需要手动再点一次解析。

多 P 任务在列表里是**层叠卡片**：2P 两张重叠，3P 及以上三张重叠，每完成一个分 P 后面的卡背会向前滚一次。

### 交互口径

- **三步式**：输入框粘贴 → 点【解析】→ 预览区选 P / 画质 / 音质 → 点【下载】。**不做剪贴板自动捕获**。
- 单 P 视频也带 `01 ` 序号前缀。
- 支持暂停 / 继续；多 P 下载中某一个 P 失败**不中断整批**。
- 安装包免 UAC（per-user 安装，可选安装路径）。

---

## 技术栈

| 项 | 选型 | 说明 |
| --- | --- | --- |
| 语言 / 框架 | C# + WPF（XAML + MVVM） | 目标框架 `net10.0`（Core）/ `net10.0-windows`（UI） |
| .NET SDK | 10.0.403 | 开发机自定义安装于 `E:\environment\NET\NET SDK10` |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | 源生成器，少样板代码 |
| DI | Microsoft.Extensions.DependencyInjection | 在 `App.xaml.cs` 用 `ServiceCollection` 装配 |
| 二维码 | QRCoder | 登录二维码渲染 |
| HTTP | `HttpClient` 单例 + `SocketsHttpHandler` | `Referer=https://www.bilibili.com` + 浏览器 UA |
| 合并 | 外部 FFmpeg | `Process` 调用，remux 成 MP4（不重新编码） |
| 凭证存储 | Windows DPAPI | 登录态加密落盘 |
| 测试 | xUnit + coverlet | 签名 / 解析 / 文件名 / 下载器（顺序 + 分片 + 续传）/ 编排 / 限速 / 同名策略 |
| 打包 | Inno Setup（`.iss`） | `dotnet publish --self-contained` 后打成安装程序 |

UI 层所有颜色都走 `Themes/Tokens.*.xaml` 的语义令牌、模板里全部 `DynamicResource`，所以换主题不需要重建界面。

---

## 架构

三层分离：**表现层（WPF）/ 核心引擎（Core，零 UI 依赖）/ 外部依赖（B 站 API、FFmpeg）**。

```
BiliDownloader.Core/
├── BiliApi/        接口客户端 + WBI 签名 + API DTO
├── Models/         领域模型（VideoTask / PageInfo / QualityOption / DownloadProgress）
├── Parse/          链接解析（BV / av / 选集 p、b23.tv 短链展开）
├── Auth/           登录态（QR 扫码登录、Cookie 登录、DPAPI 凭证）
├── Download/       分片并发下载、断点续传、重试
├── Merge/          FFmpeg 合并音视频
├── FileName/       文件名清洗与命名策略
├── Storage/        临时工作区、下载清单、原子提交
├── Orchestration/  ★ 流水线编排：解析 → 取链 → 下载 → 合并 → 落盘
├── Errors/         ★ BiliException 体系 + ErrorKind 分类
└── Utils/          HttpClient 工厂 / Cookie 处理 / 重试
```

关键设计点：

1. 文件名清洗与命名策略独立成模块，与下载、合并解耦，便于单测与多规则切换。
2. 用 DI 容器管理依赖，不在业务代码里 `new` 具体实现。
3. 内置下载器：顺序 Range 分块（连接数=1）或多连接分片（连接数>1，各片写 `.partN` 后拼回）；
   响应区间与长度校验、已有字节续传、全局令牌桶限速。
4. 跨重启恢复靠 `manifest.json`（保存目录/.bilitmp 下）：记录任务参数、每页进度与流指纹，
   重启后重建任务并校验流身份——对得上续传，对不上或无法确认就整段重下。
   指纹依次看 ETag → Last-Modified → 资源路径/文件名 + 长度（B 站 CDN 实测常不给前两者）。
4. 换主题只替换**令牌字典**（`Themes/Tokens.*.xaml`）、不重建界面树；主题色则在 `App.Resources` 顶层写同名 key 覆盖字典里的默认值。

架构评审、已修复问题与后续扩展顺序见 [架构评审.md](./架构评审.md)。

**设置已全部接入执行链**。并发分片数、全局限速、重试次数、备用节点回退、同名处理策略、命名规则与序号位数，
都在**入队时固定**成一份配置快照传给 Core（Core 不反向依赖 `AppSettings`），
所以下载途中改设置不会影响已经在跑的任务。两个例外需要知道：

- **代理模式 / 代理地址 / 请求超时**只在启动时创建 `HttpClient` 时生效，改完需**重启程序**。
- **全局限速**是共享令牌桶，所有任务与分片共用一个预算，改动对正在下载的任务**立即生效**。

---

## 目录结构

```
B站视频下载工具/
├── nuget.config                     # NuGet 还原路径固化到本地 LocalStorage
├── .gitignore                       # 忽略 bin/obj/publish/二进制/本地设计文档
├── README.md                        # 本文件
├── AGENTS.md                        # 行为规范：红线 / 代码约定 / WPF 踩坑 / 提交格式
├── 方案设计.md                       # 详细方案（含数据契约 / 磁盘规范 / 错误映射 / DoD）· 本地文档，不入库
├── 视觉规范.md                       # 视觉规范（令牌 / 字号 / 控件规格 / 动效）· 本地文档，不入库
├── design/                          # 视觉稿 · 本地文档，不入库
│   ├── mockup.html                  # 主界面
│   ├── installer.html               # 安装向导
│   └── screens.html                 # 设置 / 首启自检 / 大合集选集 / 错误态 / 断点恢复 / 关于
├── src/
│   ├── BiliDownloader.slnx          # 解决方案（.NET 10 的 slnx 格式）
│   ├── Directory.Build.props        # 全局编译属性
│   ├── BiliDownloader.UI/           # WPF 表现层 (net10.0-windows)
│   │   ├── App.xaml(.cs)            # 入口：DI 装配、全局异常钩子
│   │   ├── MainWindow.xaml(.cs)     # 主窗口：链接框 + 预览 + 任务列表
│   │   ├── SettingsWindow.xaml(.cs) # 设置窗口：基础 / 下载 / 网络 / 外观 / 关于
│   │   ├── ViewModels/              # Main / Preview / Settings / TaskItem
│   │   ├── Services/                # AppSettings、ThemeService、AccentPalette、ImageCache、凭证存储
│   │   ├── Converters/              # 状态→颜色、进度→文本、字节→人类可读
│   │   ├── Themes/                  # Tokens.Dark/Light.xaml + Components.xaml + Icons.xaml
│   │   ├── DiagnosticsShot.cs       # 开发期界面取证（--shot=... 输出 PNG + 像素统计）
│   │   └── Resources/               # 图标、样式
│   ├── BiliDownloader.Core/         # 核心引擎（纯类库，禁引用 PresentationFramework）
│   └── BiliDownloader.Tests/        # xUnit 单元测试
└── tools/
    ├── ffmpeg/                      # 内嵌 ffmpeg.exe + ffprobe.exe（二进制不入库）
    └── IconForge/                   # 图标生成器：spec.json 是唯一事实源
```

---

## 环境要求

- Windows 10 1809 及以上（推荐 Windows 11）
- .NET 10 SDK
- FFmpeg（LGPL 构建即可），放到 `tools/ffmpeg/ffmpeg.exe`；找不到时启动仍能正常运行，只是无法合并音视频

> 仓库里的 `nuget.config` 把 NuGet 包目录重定向到 `E:\environment\NET\LocalStorage`，如果你的机器上没有这个目录，改掉 `nuget.config` 里的 `globalPackagesFolder` / `http_cache_directory`，或者直接删掉这两个配置项走系统默认缓存。

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

装了 .NET Desktop Runtime 的机器上可以直接跑产物：

```
src/BiliDownloader.UI/bin/Release/net10.0-windows/BiliDownloader.UI.exe
```

打包（用户机器上免装运行时）：

```bash
dotnet publish src/BiliDownloader.UI/BiliDownloader.UI.csproj -c Release -r win-x64 --self-contained true -o ../publish
```

### 本机环境的几个坑

- **SDK 不在 PATH**。命令行里先：

  ```bash
  export DOTNET_ROOT='E:\environment\NET\NET SDK10'
  export PATH="/e/environment/NET/NET SDK10:$PATH"
  ```

- **双击 exe 报 `You must install .NET to run this application`**：SDK 装在自定义路径且没写注册表，apphost 找不到运行时。临时办法是带上上面的 `DOTNET_ROOT`；长期用 `setx DOTNET_ROOT "E:\environment\NET\NET SDK10"`（需重开终端）。**`--self-contained` 发布不受影响**，最终安装包走的就是这条路。
- **受限 shell（沙箱 / CI）缺环境变量**：`ProgramData` / `ProgramFiles` 为空会让 NuGet 还原抛 `Value cannot be null. (Parameter 'path1')`、apphost 报 `the default install location cannot be obtained`。补上即可：

  ```bash
  export ProgramData="C:\ProgramData"; export ProgramFiles="C:\Program Files"
  ```

- **CET 兼容**：历史上 Win10 19045 上编译会报 `Your Windows doesn't fully support CET`，现已随系统升级到 Windows 11 解决；`Directory.Build.props` 里的 `<EnableCetCompatibility>false>` / `<UseSharedCompilation>false>` 保留无害。
- **FFmpeg 未就位不影响启动**：`IFfmpegMerger` 在 DI 里是惰性工厂，`FfmpegLocator.Find()` 只在首次解析该服务时执行。

### 开发期取证参数

不方便肉眼观察界面时（远程桌面 / CI），用 `DiagnosticsShot` 把窗口渲染成 PNG：

```bash
BiliDownloader.UI.exe --shot=<输出目录> [--fake=<分P数>] [--fakep=<选第几集>] \
                      [--pages] [--settings] [--stack=<克隆任务数>] \
                      [--accent=<主题色Key>] [--theme=Dark] [--download] [--cancel]
```

`--fake` 会合成一份多 P 元数据直接塞给预览视图模型，**不联网**就能验证选集、画质下拉等布局；`--stack` 合成多 P 任务用于验证层叠卡背。输出目录里会额外生成 `report.txt`，包含窗口尺寸、各元素文案与像素直方图。

真实链路取证可使用 `--parse=<视频链接> --download --save-dir=<取证下载目录>`；`--save-dir` 只覆盖本次进程的下载位置，不保存到用户设置。附加 `--pause-resume` 验证暂停后继续，或附加 `--cancel` 验证中途取消。默认完成等待上限为 75 秒，必须检查报告中的最终状态，不能仅以进程退出认定成功。

### 图标工具（IconForge）

图标**不要手改** `Resources/app.ico`，改 `tools/IconForge/spec.json` 里的几何后重跑（输出直接覆盖到 `src/BiliDownloader.UI/Resources/`）：

```bash
cd tools/IconForge
dotnet build -c Release
./bin/Release/net10.0-windows/IconForge.exe render spec.json       # 出 ico + 窗口用的 256px png
./bin/Release/net10.0-windows/IconForge.exe sheet  spec.json png/sheet.png
./bin/Release/net10.0-windows/IconForge.exe verify ../../src/BiliDownloader.UI/Resources/app.ico
./bin/Release/net10.0-windows/IconForge.exe exeicon ../../src/BiliDownloader.UI/bin/Debug/net10.0-windows/BiliDownloader.UI.exe
```

改完必须做**三面对照**（任务栏 / 标题栏小标 / 托盘），只验 256px 母版或只验设计稿会漏掉「两个面不是同一个符号」。窗口内容用 `capture` 抓（走 DWM，被遮挡也能抓）：

```bash
./bin/Release/net10.0-windows/IconForge.exe capture <pid> ../../tools/IconForge/png/app.png
./bin/Release/net10.0-windows/IconForge.exe focus   <pid> [x y w h]
./bin/Release/net10.0-windows/IconForge.exe info    <pid>          # 窗口矩形 / 是否置顶 / z 序
```

---

## 设置

设置存在 `%APPDATA%\BiliDownloader\settings.json`，主要项：

| 项 | 默认值 | 说明 |
| --- | --- | --- |
| `SaveDirectory` | 我的视频 | 下载保存目录 |
| `FileNameTemplate` | IndexTitle | 序号+选集标题 / 纯标题 / 标题+UP 主，已接入引擎（入队时固定） |
| `IndexPadding` | 2 | 序号补零位数（2 或 3），已接入引擎 |
| `MaxConcurrentTasks` | 3 | 同时下载的任务数（1–8） |
| `ConnectionsPerTask` | 4 | 单任务分片并发数（1–16）。> 1 走分片并发，1 走顺序分块 |
| `GlobalSpeedLimitKbps` | 0 | 全局限速（KB/s），0 为不限；对正在下载的任务即时生效 |
| `PreferredQn` | 80（1080P） | 默认画质 |
| `PreferredCodec` | AVC | 默认编码，可改 HEVC / AV1。同一清晰度有多路码流时按它挑，挑不到再按 AVC → HEVC → AV1 兜底 |
| `PreferredAudioId` | 30280（192K） | 默认音质 |
| `ExistingFileAction` | Rename | Rename 加 (1)(2) / Skip 跳过并记为「已跳过」 / Overwrite 覆盖，三种均已接入引擎 |
| `CloseAction` | Exit | 点关闭的行为：Exit 直接退出；Minimize 隐藏窗口 + 托盘常驻 |
| `Theme` | Light | Light / Dark |
| `AccentColor` | Pink | 5 套预制主题色之一 |
| `ProxyMode` | System | System / None / Manual，**重启程序后生效** |
| `ProxyAddress` | 空 | Manual 模式下的代理地址，**重启程序后生效** |
| `RequestTimeoutSeconds` | 30 | 请求超时（5–300 秒），**重启程序后生效** |
| `MaxRetries` | 3 | 失败重试次数，已接入下载器 |
| `AutoFallbackEndpoint` | true | 主链 403/404 时自动切备用 CDN；关掉后只在主链上重试 |
| `NotifyOnComplete` | true | 完成后弹托盘气泡 |
| `AutoStart` | false | 开机自启，写 HKCU Run 项，切换即时生效 |
| `CheckUpdateOnStartup` | true | 启动查 GitHub Releases，有新版本时提示 |
| `ShowThumbnails` | true | 预览卡是否加载封面 |
| `FadeCompletedProgress` | true | 完成后进度条淡化 |
| `ReduceMotion` | false | 关掉弹层淡入等动效 |
| `LogLevel` | Info | 按级别过滤写日志目录，凭据类字段自动脱敏 |
| `Language` | zh-CN | v1 无本地化资源，控件置灰 |

> 并发数和连接数调高确实更快，但也更容易触发风控。真被限流了先把 `ConnectionsPerTask` 调到 1–2。

---

## 路线图

`M0 脚手架` → `M1 链接解析` → `M2 单视频跑通（免登录）` → `M3 下载管理器` → `M4 登录态（高清）` → `M5 体验打磨` → `v2（弹幕 / 字幕 / 批量 / 番剧）`。

当前进度：M0–M5 主链路已完成（解析、预览、多 P、下载、合并、登录、断点续传、设置、主题与小组件动效），
**设置项也已全部接入引擎**（并发分片、全局限速、重试、备用节点回退、同名策略、命名规则、代理与超时、日志、通知、开机启动、更新检查）。
跨重启恢复已落地（任务清单 manifest + 流指纹校验），易变接口（WBI 签名 / DTO 映射 / 清晰度策略）补了固定输入输出测试，
调度职责也从编排层拆出（命令串行化、暂停/停止语义澄清、移除接口、退出释放 DI）。
剩余待办：v2 的弹幕 / 字幕 / 封面下载，番剧与合集的适配，以及表现层职责拆分（MainViewModel 拆 Account / DownloadQueue）。

---

## 已知限制

- **v1 不支持**充电专属 / 付费内容，以及合集（ugc_season）——解析时会明确提示，不会静默失败。
- 没有实现弹幕 / 字幕 / 封面下载。
- 下载临时文件写在保存目录下的 `.bilitmp/`，异常退出时可能残留，手动删除即可。分片并发时该目录下还会出现
  `.partN`（分片）与 `.plan.json`（切分计划），**下载完成后自动清理**；分片期间会多占一份磁盘空间。
- **跨重启恢复的边界**：重启后能接管上次未完成的任务，但不是「无脑接着写」——
  重取直链后会比对流指纹（ETag → Last-Modified → 资源路径/文件名 + 长度），
  对不上或服务端什么都不给时**整段重下**，宁可慢也不产出拼坏的成品。
  同理，清单只按 3 秒节流记录字节进度，崩溃时最多丢最近几秒的量，续传以磁盘实际字节为准。
- 分片并发依赖服务端支持 Range。服务端忽略 Range 时该片会整段重下以保证不出错位文件。
- 清晰度上限受账号权限限制：免登录最高 1080P，更高画质需要登录或大会员。

---

## 免责声明

本项目仅供学习与个人离线备份研究，**不提供任何音视频资源**。下载内容的著作权归原作者与平台所有，请遵守平台的用户协议与当地法律，下载后请在合理期限内自行删除，不要传播或用于商业用途。仓库未指定开源许可证，默认保留所有权利。
