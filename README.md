<div align="center">
  
  <img width="4840" height="2360" alt="IMG_9811" src="https://github.com/user-attachments/assets/cef6108c-a342-474a-a17f-faa494a1f960" />
  
[![Typing SVG](https://readme-typing-svg.demolab.com?font=Fira+Code&color=F7D12A&center=true&multiline=true&repeat=false&width=435&height=55&lines=%E4%B8%80%E9%97%AA%E4%B8%80%E9%97%AA%E4%BA%AE%E6%99%B6%E6%99%B6;%E6%BB%A1%E5%A4%A9%E9%83%BD%E6%98%AF%E5%B0%8F%E6%98%9F%E6%98%9F...)](https://git.io/typing-svg)
  
</div>

> 抬头是月色，回头是同行的人

---

![Minimum OS](https://img.shields.io/static/v1?label=Minimum%20OS&message=Windows%2010%201809%20(17763)&color=blue) 

*当前仅支持 Windows*

**Luna** 是一个基于 C# / WPF 开发的 Windows 桌面个人 AI 智能体。它以一个紧凑的胶囊窗口常驻屏幕顶部，通过全局快捷键或托盘图标随时呼出。你可以像聊天一样向它提问、让它完成实际任务。

## ✨ 特性

### 🧰 工具

> 去看远方，光便替你带来答案。

Luna 内置了一套可扩展的工具框架，AI 会根据对话内容自主决定调用哪些工具。目前支持或已规划的工具分类如下：

| 分类 | 工具 | 状态 |
|------|------|------|
| 信息 | 网页搜索（Tavily）、网页阅读、剪贴板读取、当前时间 | ✅ 已实现 |
| 系统 | 文件搜索（Windows 索引 / 全盘扫描）、读取文件 | ✅ 已实现 |
| 系统（规划） | PowerShell（白名单）、写入文件（沙箱）、打开文件/应用、截图、窗口管理、音量/亮度 | 📅 远期 |
| 记忆 | 检索日记、轮次检索、长期事实 | 🧪 规划中 |
| 组织 | 待办事项（增删改查、提醒） | ✅ 已实现 |
| 通信 | 系统通知 | ✅ 已实现 |
| 输入 | 视觉、OCR、语音 | 📅 远期 |
| 开发 | 代码沙箱、Git、文件树 | 📅 远期 |

工具权限分级管理：

- **None / Low**：直接执行。
- **Medium / High**（或工具标记为需确认）：执行前弹窗，用户可选择「允许本次」「本轮对话始终允许」或「拒绝」。「始终允许」仅对当前对话生效。

### 📔 日记与记忆

> 有人替你记得那些，被时间轻轻带走的日子。

- **轮次摘要**：每轮对话结束后异步生成结构化摘要（关键点、涉及文件、工具调用等），存入数据库。
- **日记生成**：按“逻辑日”（凌晨 4 点为界，本地时间 -4h）聚合摘要，调用 AI 生成简短日记，失败时支持手动重试。
- **全文检索**：基于 SQLite FTS5，可快速搜索历史日记。
- **长期事实**：🧪 规划中——尚未实现记忆的写入与自动引用。

### ⏰ 待办与提醒

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
| Markdown 渲染 | WpfMarkdownViewer（Markdig 内核，含高亮 / 数学 / SVG / Mermaid 扩展） |
| 日志 | Serilog（File + Debug Sink） |
| 全局快捷键 | Win32 `RegisterHotKey` + 底层键盘钩子（`WH_KEYBOARD_LL`，user32 P/Invoke） |
| 窗口特效 | DWM API（`DwmSetWindowAttribute` 圆角）、`WS_EX_TOOLWINDOW` |
| 托盘 / 通知 | NativeTray（`TrayIconHost`）、Microsoft.Toolkit.Uwp.Notifications（Toast） |
| 主题控件 | DynamicAero2 |

---

## 🚀 快速开始

### 环境要求

- Windows 10 版本 1809（build 17763） 或更高 / Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

> 文件搜索优先使用系统自带的 Windows 搜索索引，未命中时回退全盘目录扫描，无需额外安装任何组件。

### 构建与运行

```bash
git clone https://github.com/Starry-3430/XiaoYueLunaAgent.git
cd XiaoYueLunaAgent
dotnet build
dotnet run --project Luna
```

### 首次配置

1. 启动 Luna，托盘出现图标。

2. 按 Ctrl + Alt + Y 呼出胶囊窗口。

3. 打开“设置 → 聊天”，填入你的 AI 提供商信息（如 DeepSeek、OpenAI 等）。

4. 保存后即可开始对话。

> API Key 使用 Windows DPAPI（CryptProtectData）加密，仅当前用户可解密，不会明文存储。

---

## 📁 项目结构

Luna/

├── Luna/

│   ├── App.xaml.cs                  # 应用入口，DI / Host / 单实例

│   ├── MainWindow.xaml              # 灵动岛胶囊窗口

│   ├── HomeWindow.xaml              # 主界面（聊天 / 设置 / 日记 / 工具）

│   ├── Views/                       # AiConnectionView、GeneralSettingsView、DiaryView

│   ├── ViewModels/                  # MVVM ViewModel

│   ├── Models/                      # 数据模型与默认提示词

│   ├── Services/

│   │   ├── Tools/                   # ITool、ToolRegistry、各工具实现

│   │   ├── OpenAiService.cs         # AI 服务与流式对话

│   │   ├── ChatGenerationService.cs # 对话/工具调用编排

│   │   ├── DiaryService.cs          # 日记生成

│   │   ├── TurnSummaryService.cs    # 轮次摘要

│   │   ├── *Repository.cs           # Session / Message / Task 仓储

│   │   └── DatabaseInitializer.cs   # 建表与 FTS5 索引

│   ├── Data/                        # DatabaseService（SQLite 连接）

│   ├── Controls/                    # 自定义控件（对话框、右键菜单、平滑滚动等）

│   └── Styles/                      # XAML 资源字典

└── Luna.sln

---

## 🗺️ 路线图

### 已完成

- [x] 灵动岛胶囊窗口（紧凑 / 展开、动画、置顶、不占任务栏）
- [x] 全局快捷键与托盘
- [x] AI 流式对话 + Markdown 渲染
- [x] 工具调用框架 + 权限确认
- [x] 基础工具：剪贴板、网页搜索、网页阅读、时间、读取文件、文件搜索、系统通知
- [x] 待办与提醒（含队列机制）
- [x] 设置界面（AI 连接、通用、工具配置）
- [x] SQLite 持久化（会话、消息、轮次）
- [x] 轮次摘要与日记生成、日记本 UI（按逻辑日聚合、翻页、FTS5 搜索）

### 进行中

- [ ] 记忆检索工具（日记检索、轮次检索）
- [ ] 长期事实存储

### 规划中

- [ ] 系统级工具：PowerShell（白名单）、写入文件（沙箱）、打开文件/应用、窗口管理、截图、音量/亮度
- [ ] 视觉与 OCR 输入
- [ ] 代码沙箱
- [ ] 多语言支持
- [ ] 自动更新

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
