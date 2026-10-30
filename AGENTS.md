# AGENTS.md — 开发行为规范

> **本文件只回答一个问题：在这个仓库里，代理什么能做、什么不能做。**
>
> 项目介绍、技术栈、目录结构、构建命令、环境注意事项 → [README.md](./README.md)
> 产品口径与接口契约 → `方案设计.md`；视觉令牌与组件规格 → `视觉规范.md`
> （后两份是本地文档，被 `.gitignore` 排除，未随仓库发布）

---

## 1. 红线（不可协商）

| # | 禁止 | 说明 |
| --- | --- | --- |
| 1 | 未经用户**当次明确指令**执行 `git commit` / `git push` / 建 PR | 改动只留工作区。用户说「本地提交」时**不许顺手推远程**。之前某次允许过提交，不代表后续默认可提交。 |
| 2 | 提交信息、README、文档、代码注释里出现 AI 署名 | `WorkBuddy` / `Cursor` / `Copilot` 等一律不得进入 Contributors、`user.name`、`user.email`、`Co-Authored-By`、commit body。Contributors 只能有作者本人。 |
| 3 | Core 工程引用 `PresentationFramework` 等 WPF 程序集 | Core 必须能在控制台 / 测试里独立运行，UI 类型不许跨过这条线。 |
| 4 | 擅自 `restore`、安装或升级 NuGet 包、改 `Directory.Build.props` / `nuget.config` / `.gitignore` | 依赖与环境变更先请示。确有必要再改，并在回复里点名改了哪一处。 |
| 5 | 内置公共账号、绕过付费、绕过风控 | 仅个人学习使用，遵守 B 站用户协议。付费 / 合集内容解析要明确提示不支持，**不静默失败**。 |
| 6 | 交付前不验证 | `dotnet build` 必须 **0 error 0 warning**，再跑 `dotnet test` 全绿。 |

---

## 2. 本机命令（照抄，别自己拼）

```bash
export DOTNET_ROOT='E:\environment\NET\NET SDK10'
export PATH="/e/environment/NET/NET SDK10:$PATH"
# 受限 shell 缺这两个变量时，NuGet 会抛 "Value cannot be null (path1)"、apphost 报找不到安装位置
export ProgramData="C:\ProgramData"; export ProgramFiles="C:\Program Files"

cd <仓库根>
dotnet build src/BiliDownloader.slnx --no-restore            # ⚠️ 必须 --no-restore
dotnet test  src/BiliDownloader.slnx --no-restore --no-build
```

- **一律 `--no-restore`**：本机 shell 里 restore 必失败，包已在本地 `LocalStorage`。
- 编译报 exe 被占用（MSB3027）时，先 `taskkill` 掉正在运行的 `BiliDownloader.UI.exe`。
- 起进程跑界面要带 `DOTNET_ROOT`，否则 apphost 找不到运行时。详见 README「环境要求」。

---

## 3. 交付前必做

- **UI 改动一律以实测取证为准**，不以编译通过为准。文案没替换、样式没生效、隐式的 FX 丢失、布局多出滚动条，都能通过编译。可用度量代替肉眼，例如用 `documentElement.scrollHeight - clientHeight` / 窗口 `scrollHeight` 判断多余的滚动条。
- **同一个文件的多次编辑必须串行**。并行发多个编辑会各自基于同一份快照读→写，后写的覆盖先写的，且**不报任何错**。不同文件并行没问题。
- **删控件的清单**，漏一项就是编译期或运行期报错：
  XAML 节点 → 所在的 `RowDefinition` / 布局依赖 → 用到的 `DataTemplate` / `Style` → **code-behind 里按 `x:Name` 引用它的成员**。最后一步最容易漏。
- 回复里要点名改了哪些文件，不要笼统说「已修改」。

---

## 4. 代码约定

- **分层**：UI 引用 Core，反向禁止；业务编排放 `Core/Orchestration`，UI 只做呈现。
- **依赖走 DI**（`App.xaml.cs` 用 `ServiceCollection` 装配），业务代码里不 `new` 具体实现。
- **易变项集中成模块 + 单测锁定**：WBI 签名、DASH 直链、清晰度 id 会随 B 站风控变化，不许散落在各处。
- **MVVM**：CommunityToolkit.Mvvm 源生成器（`[ObservableProperty]` / `[RelayCommand]`）；命令可用性变化记得 `NotifyCanExecuteChangedFor`。
- **颜色全部走语义令牌**（`Themes/Tokens.*.xaml`），模板里一律 `DynamicResource`，**禁止硬编码色值**。换主题不能靠重建界面。
- **语义色一致**：红色只用于真破坏性操作（取消任务 / 清空记录）；普通关闭、"取消弹框"、标题栏按钮用中性色，不要用红。
- **命名与兜底**：文件名默认「序号 + 选集标题」，非法字符清洗集中在 `Core/FileName`。
- **错误要有映射**：异常走 `Core/Errors` 的 `ErrorKind` 分类 → UI 展示人话提示，不要把原始异常抛给用户。

---

## 5. WPF 已踩过的坑（禁止重犯）

- `Border.CornerRadius` **不像 CSS 会自动收敛到 `高度/2`**：6px 高的进度条写 `CornerRadius=99` 会渲染成楔形尖角，必须显式写「高度的一半」。
- `DisplayMemberPath` 与 `ItemTemplate` **互斥**，同时设置运行时抛 `InvalidOperationException`。
- 自定义了 `ComboBox` 的 `ControlTemplate` 后，`DisplayMemberPath` **不再生效**（选中项显示成类型名），必须改用 `ItemTemplate`。
- `Path` 画叉（`M1,1 L9,9 M9,1 L1,9`）是**描边几何**，要用 `Stroke` + `StrokeThickness`，用 `Fill` 画不出来。
- XAML 注释里不能出现 `--`（拿它当分隔线会报 MC3000）。
- `WindowStyle="None"` + `WindowStartupLocation="CenterScreen"` 在非 100% DPI 下会把窗口算到屏幕外；纠偏要走 Win32（`MonitorFromWindow` + `GetMonitorInfo` + `SetWindowPos`），**别信 WPF 的 `Left` / `Top`**——窗口在屏外时这两个值本身就是错的。
- 只有**隐式**的 `<Style TargetType="ScrollBar">` 才能覆盖所有 `ScrollViewer`；定义了具名样式却没被引用时，跑的是系统默认粗条。
- `DataTemplate` 里用 `Storyboard` 的 `TargetName` 依赖 namescope，容易炸；**动画值尽量在 ViewModel 里算**，XAML 只绑定结果。

---

## 6. 提交规范

### 6.1 作者身份

- Contributors **只能有作者本人**。AI 辅助工具不得出现在 `user.name` / `user.email` / `Co-Authored-By` / commit body / 文档署名里。
- 提交前确认本机身份：

  ```bash
  git config user.name    # 应为作者本人，不能含 workbuddy / cursor / bot 等字样
  git config user.email
  ```

### 6.2 commit message 格式

**按实际涉及的类型分行写，一行一个类型，用中文全角冒号「：」。不涉及的项不要写。**

| 类型 | 含义 |
| --- | --- |
| `feat` | 增加功能 |
| `fix` | bug 修复 |
| `style` | 样式调整 |
| `perf` | 优化代码 |
| `refactor` | 重构 |
| `docs` | 优化文档 |
| `chore` | 日常（升级 SDK、移除废弃代码） |

规则：

1. 标题仍是**一行式**概述，后面空一行再列类型行。
2. 类型行 = `类型：` + 该类型下的具体改动，一行写完。
3. 只列这次真正动了的类型——只改 XAML 就只写 `style：`，不要凑数写 `feat` / `refactor`。
4. 涉及多类改动时并行多行，按 feat → fix → style → perf → refactor → docs → chore 的顺序排。

示例：

```
设置页支持主题色切换

feat：外观页新增 5 套预制主题色下拉，切换后全窗口令牌即时生效
fix：深色主题下设置页导航未选中项是黑字，补 Foreground 令牌
style：全选 / 反选 / 恢复默认改为带描边填充的次级按钮
docs：README 补充 AccentColor 配置项说明
```

### 6.3 提交红线

未经用户**明确指令**，不得 `git commit` / `git push` / 建 PR（见 §1）。改动留在工作区，等用户说「提交 / 推送」再动手。
