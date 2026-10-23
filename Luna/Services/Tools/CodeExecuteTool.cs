// 工具：在独立进程中运行 Python 代码（沙箱 + 超时 + 输出限制）。
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Luna.Models;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>
/// 在独立进程中执行 Python 代码（<c>python.exe</c>，不使用 Assembly.Load / 动态编译）。
/// 沙箱为"尽力而为"：跨进程无法硬隔离网络与文件系统，仅以正则黑名单 + 独立临时目录 + 超时兜底，非绝对安全。
/// 超时 10 秒（超时杀进程树），输出上限 100 KB（超出即停止累计并标记）。
/// TODO: 256 MB 内存硬限制（通过 JobObject CreateJobObject / SetInformationJobObject），初期用超时兜底。
/// </summary>
public class CodeExecuteTool : ITool
{
    private const int TimeoutMs = 10_000;
    private const int MaxOutputChars = 100 * 1024; // 100 KB

    /// <summary>沙箱逃逸防护：命中任意一条即拒绝执行。跨进程无法硬隔离，属"尽力而为"。</summary>
    private static readonly (string Name, Regex Pattern)[] ForbiddenPatterns =
    [
        ("import socket", new(@"\bimport\s+socket\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("import urllib", new(@"\b(import|from)\s+urllib", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("import requests", new(@"\bimport\s+requests\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("import httpx", new(@"\bimport\s+httpx\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("subprocess", new(@"\bsubprocess\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("shutil", new(@"\bshutil\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("pathlib", new(@"\bpathlib\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("os module", new(@"\bimport\s+os\b|\bfrom\s+os\b|\bos\.", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("open()", new(@"\bopen\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("__import__", new(@"\b__import__\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("eval/exec/compile", new(@"\b(eval|exec|compile)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    ];

    private const string SchemaTemplate =
        """{"type":"object","properties":{"code":{"type":"string","description":"$code$"}},"required":["code"]}""";

    private readonly AiSettings _settings;
    private readonly ILogger<CodeExecuteTool> _logger;

    public string Name => "code_execute";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate.Replace("$code$", Param("Tool_CodeExecute_Param_Code")));
    public ToolRiskLevel Risk => ToolRiskLevel.High;

    public CodeExecuteTool(AiSettings settings, ILogger<CodeExecuteTool> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var code = GetString(args, "code");
        if (string.IsNullOrWhiteSpace(code))
            return Task.FromResult(ToolResult.Error(Text("Tool_Msg_MissingParam", "code")));

        // 完全访问模式：允许网络、系统操作、动态执行等能力，不再做正则黑名单拦截。
        if (!_settings.FullAccessEnabled)
        {
            var blocked = FindForbidden(code);
            if (blocked is not null)
                return Task.FromResult(ToolResult.Error(
                    Text("Tool_CodeExecute_Msg_Forbidden", blocked)));
        }

        return Task.Run(() => Run(code, ct), ct);
    }

    private ToolResult Run(string code, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return ToolResult.Error(Text("Tool_Msg_Cancelled"));

        var sandboxDir = Path.Combine(Path.GetTempPath(), $"luna_run_{Guid.NewGuid():N}");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"luna_{Guid.NewGuid():N}.py");

        try
        {
            Directory.CreateDirectory(sandboxDir);
            File.WriteAllText(scriptPath, code);

            var psi = new ProcessStartInfo
            {
                FileName = "python.exe",
                WorkingDirectory = sandboxDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(scriptPath);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            _logger.LogInformation("执行 Python 代码");

            Process process;
            try
            {
                process = Process.Start(psi)!;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动 python.exe 失败");
                return ToolResult.Error(Text("Tool_CodeExecute_Msg_StartFailed", ex.Message));
            }

            var stopwatch = Stopwatch.StartNew();
            using (process)
            using (ct.Register(() => TryKill(process)))
            {
                var stdOutTask = ReadCappedAsync(process.StandardOutput);
                var stdErrTask = ReadCappedAsync(process.StandardError);

                var exited = process.WaitForExit(TimeoutMs);
                var timedOut = !exited;
                if (timedOut)
                    TryKill(process);

                stopwatch.Stop();

                var (stdOut, outTruncated) = stdOutTask.GetAwaiter().GetResult();
                var (stdErr, _) = stdErrTask.GetAwaiter().GetResult();

                var exitCode = timedOut ? -1 : process.ExitCode;
                var content = Format(exitCode, timedOut, stopwatch.Elapsed, stdOut, stdErr, outTruncated);

                if (timedOut)
                {
                    _logger.LogWarning("Python 代码执行超时，已终止");
                    return ToolResult.Error(content);
                }

                return exitCode == 0 ? ToolResult.Ok(content) : ToolResult.Error(content);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行 Python 代码失败");
            return ToolResult.Error(Text("Tool_CodeExecute_Msg_Failed", ex.Message));
        }
        finally
        {
            TryDeleteFile(scriptPath);
            TryDeleteDirectory(sandboxDir);
        }
    }

    /// <summary>读取流并累计到 100 KB；超出后继续读取（丢弃）以免阻塞子进程，并标记截断。</summary>
    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(StreamReader reader)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length));
            }
            catch
            {
                break;
            }

            if (read == 0) break;

            if (sb.Length < MaxOutputChars)
            {
                var take = Math.Min(read, MaxOutputChars - sb.Length);
                sb.Append(buffer, 0, take);
                if (take < read)
                    truncated = true;
            }
            else
            {
                truncated = true;
            }
        }

        return (sb.ToString(), truncated);
    }

    private static string Format(int exitCode, bool timedOut, TimeSpan elapsed, string stdOut, string stdErr, bool truncated)
    {
        var sb = new StringBuilder();
        sb.Append(Text("Tool_CodeExecute_Msg_ExitCode")).Append(timedOut ? Text("Tool_CodeExecute_Msg_TimedOut") : exitCode.ToString()).AppendLine();
        sb.Append(Text("Tool_CodeExecute_Msg_Elapsed")).Append(elapsed.TotalSeconds.ToString("0.0")).AppendLine("s");
        sb.AppendLine();
        sb.AppendLine("=== stdout ===");
        sb.AppendLine(string.IsNullOrEmpty(stdOut) ? Text("Tool_CodeExecute_Msg_Empty") : stdOut.TrimEnd());
        if (truncated)
            sb.AppendLine(Text("Tool_CodeExecute_Msg_Truncated"));
        sb.AppendLine();
        sb.Append("=== stderr ===").AppendLine();
        sb.Append(string.IsNullOrEmpty(stdErr) ? Text("Tool_CodeExecute_Msg_Empty") : stdErr.TrimEnd());
        return sb.ToString();
    }

    private static string? FindForbidden(string code)
    {
        foreach (var (name, pattern) in ForbiddenPatterns)
        {
            if (pattern.IsMatch(code))
                return name;
        }
        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已退出
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略清理失败
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 忽略清理失败
        }
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? string.Empty;
        return string.Empty;
    }
}
