<div align="center">

<img width="2420" height="1180" alt="IMG_9811_small" src="https://github.com/user-attachments/assets/196a7882-26e0-4b0a-98a8-9fe51d9dbe39" />

[![Typing SVG](https://readme-typing-svg.demolab.com?font=Fira+Code&color=E38A20&center=true&multiline=true&repeat=false&width=435&height=55&lines=%E4%B8%80%E9%97%AA%E4%B8%80%E9%97%AA%E4%BA%AE%E6%99%B6%E6%99%B6;%E6%BB%A1%E5%A4%A9%E9%83%BD%E6%98%AF%E5%B0%8F%E6%98%9F%E6%98%9F...)](https://git.io/typing-svg)
  
</div>

> 抬头是月色，回头是同行的人

---

![Minimum OS](https://img.shields.io/static/v1?label=Minimum%20OS&message=Windows%2010%201809%2B&color=blue)

*当前仅支持 Windows*

> 当前版本：**v0.1.0.0（公开测试版）**

**Luna** 是一个基于 C# / WPF 开发的 Windows 桌面个人 AI 智能体。它以一个紧凑的胶囊窗口常驻屏幕顶部，通过全局快捷键或托盘图标随时呼出。你可以像聊天一样向它提问、让它完成实际任务。

## ✨ 特性

### 🧰 工具

<img width="4800" height="400" alt="IMG_9814" src="https://github.com/user-attachments/assets/f34bddc4-d293-44a3-b45d-ca56e3f71c4a" />

> 去看远方，光便替你带来答案。

Luna 内置了一套可扩展的工具框架，AI 会根据对话内容自主决定调用哪些工具。目前支持或已规划的工具分类如下：

| 分类 | 工具 | 状态              |
|------|------|-------------------|
| 信息 | 网页搜索（Tavily）、网页阅读、剪贴板读取、新闻/日历 | ✅ 已实现         |
| 系统 | 文件搜索（Windows 索引 / 全盘扫描）、读取文件（文本 / PDF / Office 文档，MarkItDown 转换，支持关键词搜索与分段读取） | ✅ 已实现         |
| 系统 | PowerShell（白名单）、写入文件（沙箱）、打开文件/应用 | ✅ 已实现         |
| 系统 | 截图（窗口排除需 Windows 10 2004+，旧版自动回退移屏） | ✅ 已实现         |
| 系统 | 窗口管理、音量/亮度 | ⏳ 进行中     |
| 记忆 | 检索日记、轮次检索、长期事实 | ✅ 已实现         |
| 组织 | 待办事项（增删改查、提醒）、剪贴板历史 | ✅ 已实现         |
| 通信 | 系统通知 | ✅ 已实现         |
| 输入 | 图片附件与截图视觉（需模型支持视觉） | ✅ 已实现         |
| 输入 | OCR、语音输入 | 📅 远期           |
| 开发 | 代码沙箱、Git、文件树 | ✅ 已实现（部分） |

工具权限分级管理：

- **None / Low**：直接执行。
- **Medium / High**（或工具标记为需确认）：执行前弹窗，用户可选择「允许本次」「本轮对话始终允许」或「拒绝」。「始终允许」仅对当前对话生效。

### 📔 日记与记忆

<img width="4800" height="400" alt="5C735DEE39EA5CE92BCF7B0101696B40" src="https://github.com/user-attachments/assets/bf0e734f-c7e1-410c-8200-bf572a74dd93" />


> 有人替你记得那些，被时间轻轻带走的日子。

- **轮次摘要**：每轮对话结束后异步生成结构化摘要（关键点、涉及文件、工具调用等），存入数据库。
- **日记生成**：按“逻辑日”（凌晨 4 点为界，本地时间 -4h）聚合摘要，调用 AI 生成简短日记，失败时支持手动重试。
- **全文检索**：基于 SQLite FTS5，可快速搜索历史日记。
- **长期事实**：通过 `remember_fact` / `list_facts` / `forget_fact` 工具维护；每轮会按当前话题自动检索最多 5 条相关事实注入提示词。自动事实抽取仍在计划中。

### ⏰ 待办与提醒

<img width="4800" height="400" alt="9FCD168FE8C36F8FE20C3BEAB10E6453" src="https://github.com/user-attachments/assets/5dc836c7-169f-4f71-bfbe-cbd3de5e9231" />

> 把还没做完的事交给明天，把今天留给眼前的风。

- 通过自然语言添加待办：“帮我记一下明天下午三点开会”。
- 支持截止时间与提醒时间，提醒触发时以胶囊弹出 + 唤醒动画呈现。
- 提醒队列机制：胶囊正在输入或被占用时自动排队，不打断用户操作。

---

## 🧱 技术栈

| 层 | 技术 |
|----|------|
| 语言 / 运行时 | **C# 14 / .NET 10**（`net10.0-windows10.0.19041.0`，未指定 `LangVersion`，取 SDK 默认值） |
| UI 框架 | WPF |
| MVVM | CommunityToolkit.Mvvm |
| 依赖注入 / 托管 | Microsoft.Extensions.Hosting / DependencyInjection |
| 数据库 | SQLite（Microsoft.Data.Sqlite） + Dapper |
| 全文检索 | SQLite FTS5 |
| 文档转换 | ManagedCode.MarkItDown（PDF / Office Open XML / HTML / 文本等转 Markdown） |
| Markdown 渲染 | WpfMarkdownViewer（Markdig 内核，含高亮 / 数学 / SVG / Mermaid 扩展） |
| 日志 | Serilog（File + Debug Sink） |
| 全局快捷键 | Win32 `RegisterHotKey` + 底层键盘钩子（`WH_KEYBOARD_LL`，user32 P/Invoke） |
| 窗口特效 | DWM API（`DwmSetWindowAttribute` 圆角）、`WS_EX_TOOLWINDOW` |
| 托盘 / 通知 | NativeTray（`TrayIconHost`）、Microsoft.Toolkit.Uwp.Notifications（Toast） |
| 主题控件 | DynamicAero2 |
| 安装包 | Inno Setup 6 |

---

## 🚀 快速开始

### 下载安装包（推荐）

从 [Releases](https://github.com/Starry-3430/XiaoYueLunaAgent/releases) 下载：

| 包 | 说明 | 需要 |
|----|------|------|
| `Luna-v0.1.0.0-Setup.exe` | 安装版（推荐） | 无需安装 .NET 运行时；默认安装到 `%LOCALAPPDATA%\Programs\Luna` |
| `Luna-win-x64.zip` | 自包含免安装版 | 无需安装 .NET 运行时，解压即用 |
| `Luna-win-x64-fdd.zip` | 框架依赖免安装版 | 需安装 [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) |

1. 运行安装程序，或解压对应的免安装 ZIP。
2. 从开始菜单 / 桌面 / 解压目录启动 `Luna.exe`。
3. 首次启动会显示欢迎向导，按提示填写 AI 提供商、Base URL、模型与 API Key；也可以稍后在「设置 → 聊天」中修改。

> **SmartScreen 提示**：当前 Beta 未做代码签名，Windows 可能提示「Windows 已保护你的电脑」。点击「更多信息」→「仍要运行」即可。
>
> **系统要求**：Windows 10 1809（build 17763）或更高 / Windows 11。截图工具的「Luna 窗口不进入截图」使用 `WDA_EXCLUDEFROMCAPTURE`，需要 **Windows 10 2004（build 19041）或更高**；旧版 Windows 会自动回退为临时移屏。
>
> **开机自启**：可在「设置 → 通用」中开启，可选择常规开机自启或带管理员权限的的开机自启；如不需要，请保持关闭。

### 从源码构建

环境要求：

- Windows 10 1809（build 17763）或更高 / Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 构建安装包需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)

```bash
git clone https://github.com/Starry-3430/XiaoYueLunaAgent.git
cd XiaoYueLunaAgent
dotnet build
dotnet run --project Luna
```

生成自包含发布目录和安装包：

```powershell
pwsh .\scripts\publish.ps1 -Mode selfcontained
pwsh .\scripts\build-installer.ps1 -SkipPublish
```

### 首次配置

1. 启动 Luna，托盘出现图标。
2. 按 `Ctrl + Alt + Y` 呼出胶囊窗口（可在设置中修改快捷键）。
3. 打开「设置 → 聊天」，填入你的 AI 提供商信息（如 DeepSeek、OpenAI 等）。
4. 保存后即可开始对话。

> API Key 使用 Windows DPAPI（CryptProtectData）加密，仅当前 Windows 用户可解密，不会以明文写入配置文件。

---

## 🔐 数据与隐私

Luna 是本地优先应用：会话、日记、待办、长期事实和附件默认保存在本机。联网行为仅发生在以下场景：调用你配置的 AI 提供商、网页搜索（Tavily）、网页阅读 / 新闻（目标网站或 BBC News RSS）。

### 本地数据位置

| 路径 | 内容 | 说明 |
|------|------|------|
| `%APPDATA%\Luna\settings.json` | 设置、提供商信息、API Key 密文 | API Key（含 Tavily）/ 代理密码使用 Windows DPAPI 加密，仅当前 Windows 用户可解密 |
| `%APPDATA%\Luna\database\LunaData.db` | 会话、消息、轮次摘要、日记、长期事实、待办、工具审计、附件元数据 | **明文 SQLite**，另有 `-wal` / `-shm` 文件 |
| `%APPDATA%\Luna\database\LunaData.db.before-restore-*` | 导入备份前的数据库快照 | 明文，导入数据时生成 |
| `%LOCALAPPDATA%\Luna\blobs\` | 附件原始内容 | 按 SHA-256 命名，明文存储 |
| `%LOCALAPPDATA%\Luna\logs\` | 运行日志 | 按天滚动，最多保留 7 个文件；记录调用状态、耗时与错误信息，不记录完整对话 |
| `%LOCALAPPDATA%\Luna\backups\` | 自动备份 ZIP | 每天 03:00 生成，最多保留 7 份（错过则下次启动补做） |
| `%LOCALAPPDATA%\Luna\cache\markitdown\` | 文档转换临时缓存 | 可安全删除，Luna 会按需重建 |
| `%LOCALAPPDATA%\Luna\.installed` | 首次运行标记 | 删除后会重新进入欢迎向导 |

> `settings.json` 和数据库位于漫游目录 `%APPDATA%`；如果 Windows 账户启用了漫游配置文件（常见于企业域环境），这些明文数据可能随配置文件同步到服务器。附件、日志和备份位于 `%LOCALAPPDATA%`，不会漫游。

### 隐私边界

- **数据库、日记、长期事实、附件和日志均为明文**，没有整库加密；能访问此 Windows 账户或磁盘的人可以直接读取。
- 对话内容、与当前话题相关的长期事实、附件内容会发送给你配置的 AI 提供商；启用网页搜索时会请求 Tavily，网页阅读 / 新闻工具会进行联网搜索。
- API Key 使用 DPAPI 加密保存；日志不会记录 API Key、完整对话或工具参数，但仍可能包含调用状态、耗时、错误响应和文件路径等诊断信息，请勿把日志或导出文件发给不可信的人。
- 数据导出和自动备份 **不包含 `settings.json`**（避免密钥跨设备复用），并且 ZIP **没有加密**。
- 「设置 → 调试 → 清除数据库」目前只清理会话、消息、日记、长期事实、附件元数据和轮次摘要；不会删除待办、工具审计日志、附件 blob、日志和备份。彻底清理请手动删除数据目录。

### 卸载

**安装版**：打开 Windows「设置 → 应用 → 已安装的应用」，找到 Luna 并卸载。卸载程序会询问是否同时删除个人数据，默认保留。

**免安装 ZIP 版**：

1. 如开启过开机自启，先在「设置 → 通用」中关闭。
2. 托盘右键 → 关闭，退出 Luna。
3. 删除解压后的程序目录。
4. 如需彻底删除个人数据，再手动删除 `%APPDATA%\Luna` 和 `%LOCALAPPDATA%\Luna`。

> 删除数据目录不可恢复；请先在设置页的「数据备份」区域导出备份。

---

## ⚠️ Beta 已知问题

> 这是首个公开测试版，数据格式与功能行为仍可能调整；升级前请先在设置页导出备份。

- 会话历史尚未做上下文预算与滚动摘要，极长会话可能触及模型上下文上限或增加费用。
- 工具结果超过 5000 字会被截断，暂不支持按 ID 回读完整结果。
- 代码沙箱目前只有超时限制，尚未施加 256 MB 内存硬限制。
- 更新通过下载完整安装包进行，暂不支持增量更新；安装包未签名，可能被 SmartScreen 提示未知发布者。
- 视觉仅支持图片 / 截图输入（需模型支持视觉）；OCR 与语音输入尚未实现。
- 未提供整库加密或隐身会话；敏感数据请自行保护 Windows 账户与磁盘。

---

## 📁 项目结构

```
Luna/
├── Luna/
│   ├── App.xaml.cs                  # 应用入口，DI / Host / 单实例
│   ├── MainWindow.xaml              # 灵动岛胶囊窗口
│   ├── HomeWindow.xaml              # 主界面（聊天 / 设置 / 日记 / 工具）
│   ├── Views/                       # AiConnectionView、GeneralSettingsView、DiaryView、AttachmentManagerView
│   ├── ViewModels/                  # MVVM ViewModel
│   ├── Models/                      # 数据模型与默认提示词
│   ├── Services/
│   │   ├── Tools/                   # ITool、ToolRegistry、各工具实现
│   │   ├── Data/                    # AttachmentRepository、FileStorageService（内容寻址存储）
│   │   ├── OpenAiService.cs         # AI 服务与流式对话
│   │   ├── ChatGenerationService.cs # 对话/工具调用编排
│   │   ├── MarkItDownConverterService.cs # 附件文档转换（重试 / 取消 / 后台线程）
│   │   ├── AttachmentHelper.cs      # 附件选择、上传规则、大小格式化
│   │   ├── DiaryService.cs          # 日记生成
│   │   ├── TurnSummaryService.cs    # 轮次摘要
│   │   ├── *Repository.cs           # Session / Message / Task 仓储
│   │   └── DatabaseInitializer.cs   # 建表与 FTS5 索引
│   ├── Data/                        # DatabaseService（SQLite 连接）
│   ├── Controls/                    # 自定义控件（对话框、右键菜单、平滑滚动等）
│   └── Styles/                      # XAML 资源字典
├── installer/                       # Inno Setup 安装脚本
├── licenses/                        # 字体等第三方许可证
└── Luna.sln
```

---

## 🗺️ 路线图

### 已完成

- [x] 灵动岛胶囊窗口（紧凑 / 展开、动画、置顶、不占任务栏）
- [x] 全局快捷键与托盘
- [x] AI 流式对话 + Markdown 渲染
- [x] 工具调用框架 + 权限确认
- [x] 基础工具：剪贴板、网页搜索、网页阅读、读取文件、文件搜索、系统通知
- [x] 待办与提醒（含队列机制）
- [x] 设置界面（AI 连接、通用、工具配置）
- [x] SQLite 持久化（会话、消息、轮次）
- [x] 轮次摘要与日记生成、日记本 UI（按逻辑日聚合、翻页、FTS5 搜索）
- [x] 附件与文档（多格式转换、内容寻址存储、Token 预算与智能截断、拖拽上传、附件管理）
- [x] 代码沙箱
- [x] 多语言支持
- [x] 系统提示词自动注入时间、操作系统、语言等运行环境，并包含提示词注入防范规则
- [x] 记忆检索工具（日记检索、轮次检索）
- [x] 长期事实存储
- [x] 系统级工具：PowerShell（白名单）、写入文件（沙箱）、打开文件/应用
- [x] 系统级工具：截图（Windows 10 2004+ 窗口排除，旧版移屏兜底）

### 计划中

- [ ] OCR 与语音输入
- [ ] 自动事实抽取与事实管理 UI

---

## 🤝 贡献

欢迎提交 Issue 与 PR。在提交 PR 前，请确保：

- 代码通过 dotnet build

- 新增工具遵循 ITool 接口，并标注风险等级

- 新增功能附带手动测试步骤

---

## 📄 许可证

本项目采用 MIT 许可证。详见 LICENSE。

---

## 🙏 致谢  
            
- [WpfMarkdownViewer](https://github.com/QuickerOrg/WpfMarkdownViewer)  
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)  
- [Dapper](https://github.com/DapperLib/Dapper)  
- [Serilog](https://serilog.net/)
