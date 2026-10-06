# 第三方组件声明 / Third-Party Notices

本项目（Luna）在构建与运行时依赖以下第三方开源组件。各组件的版权归其各自作者所有，
并按其原始许可证发布。完整的许可证文本可在对应项目的仓库或许可证链接中获取。

## 运行时依赖

| 组件 | 许可证 | 版权 / 项目 |
|------|--------|-------------|
| CommunityToolkit.Mvvm | MIT | Microsoft — https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.DependencyInjection | MIT | Microsoft — https://github.com/dotnet/runtime |
| Microsoft.Extensions.Hosting | MIT | Microsoft — https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging | MIT | Microsoft — https://github.com/dotnet/runtime |
| Microsoft.Data.Sqlite | MIT | Microsoft — https://github.com/dotnet/efcore |
| System.Data.OleDb | MIT | Microsoft — https://github.com/dotnet/runtime |
| NativeTray | MIT | ema — https://github.com/emako/NativeTray |
| DynamicAero2 | MIT | ManjuSummoner — https://github.com/manju-summoner/DynamicAero2 |
| Microsoft.Toolkit.Uwp.Notifications | MIT | Microsoft / .NET Foundation — https://github.com/CommunityToolkit/WindowsCommunityToolkit |
| Serilog | Apache-2.0 | Serilog Contributors — https://serilog.net/ |
| Serilog.Extensions.Hosting | Apache-2.0 | Serilog Contributors — https://github.com/serilog/serilog-extensions-hosting |
| Serilog.Sinks.Debug | Apache-2.0 | Serilog Contributors — https://github.com/serilog/serilog-sinks-debug |
| Serilog.Sinks.File | Apache-2.0 | Serilog Contributors — https://github.com/serilog/serilog-sinks-file |
| Dapper | Apache-2.0 | Sam Saffron, Marc Gravell, Nick Craver — https://github.com/DapperLib/Dapper |
| ManagedCode.MarkItDown | MIT | ManagedCode — https://github.com/managedcode/markitdown |

## Markdown 渲染库（`lib/WpfMarkdownViewer`，MIT 分支）

`lib/WpfMarkdownViewer` 为 [QuickerOrg/WpfMarkdownViewer](https://github.com/QuickerOrg/WpfMarkdownViewer)
的本地副本 / 分支，遵循其 MIT 许可证（版权归 CuiLiang 及 QuickerOrg 贡献者所有，
详见 `lib/WpfMarkdownViewer/LICENSE`）。其自身依赖如下：

| 组件 | 许可证 | 版权 / 项目 |
|------|--------|-------------|
| Markdig | BSD-2-Clause | Alexandre Mutel — https://github.com/xoofx/markdig |
| TextMateSharp | MIT | Daniel Peñalba — https://github.com/danipen/TextMateSharp |
| TextMateSharp.Grammars | MIT | Daniel Peñalba — https://github.com/danipen/TextMateSharp |
| WpfMath (xaml-math) | MIT AND OFL-1.1 | ForNeVeR — https://github.com/ForNeVeR/xaml-math |
| Mermaider | MIT | Nullean and contributors — https://github.com/nullean/mermaider |
| Mostlylucid.Dagre | MIT | scottgal — https://github.com/scottgal/mostlylucid.dagre |
| SharpVectors.Reloaded | BSD-3-Clause | Elinam LLC — https://github.com/ElinamLLC/SharpVectors |

## 文档转换库（ManagedCode.MarkItDown，MIT）

[ManagedCode.MarkItDown](https://github.com/managedcode/markitdown) 遵循 MIT 许可证（版权归 ManagedCode SAS 所有），
用于把 PDF / Office Open XML / HTML / 文本等文档转换为 Markdown。其自身依赖如下
（其中各云服务商的 OCR / 语音 SDK 仅在启用相应功能时才会实际使用）：

| 组件 | 许可证 | 版权 / 项目 |
|------|--------|-------------|
| AngleSharp | MIT | AngleSharp contributors — https://github.com/AngleSharp/AngleSharp |
| DocumentFormat.OpenXml | MIT | Microsoft — https://github.com/dotnet/Open-XML-SDK |
| PdfPig | Apache-2.0 | UglyToad and contributors — https://github.com/UglyToad/PdfPig |
| PDFtoImage | MIT | Dtronix — https://github.com/sungaila/PDFtoImage |
| SkiaSharp | MIT | Microsoft / Mono Project — https://github.com/mono/SkiaSharp |
| MimeKit | MIT | Jeffrey Stedfast — https://github.com/jstedfast/MimeKit |
| Sep | MIT | Nietras — https://github.com/nietras/Sep |
| YoutubeExplode | MIT | Oleksii Holub — https://github.com/Tyrrrz/YoutubeExplode |
| ManagedCode.MimeTypes | MIT | ManagedCode — https://github.com/managedcode/MimeTypes |
| ManagedCode.Storage.Core / .FileSystem / .Azure / .Aws / .Gcp | MIT | ManagedCode — https://github.com/managedcode/Storage |
| Microsoft.Extensions.AI | MIT | Microsoft — https://github.com/dotnet/extensions |
| AWSSDK.S3 / .Rekognition / .Textract / .TranscribeService | Apache-2.0 | Amazon Web Services — https://github.com/aws/aws-sdk-net |
| Azure.Identity / Azure.AI.FormRecognizer / Azure.AI.Vision.ImageAnalysis | MIT | Microsoft — https://github.com/Azure/azure-sdk-for-net |
| Google.Cloud.DocumentAI.V1 / .Vision.V1 / .Speech.V1 | Apache-2.0 | Google — https://github.com/googleapis/google-cloud-dotnet |

## 构建期依赖

| 组件 | 许可证 | 版权 / 项目 |
|------|--------|-------------|
| Microsoft.SourceLink.GitHub | MIT | Microsoft — https://github.com/dotnet/sourcelink |
