// 工具：执行只读 PowerShell 命令（写操作黑名单，每次需确认）。
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Luna.Models;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>
/// 执行只读 PowerShell 命令。命令必须以只读动词开头、不得包含写操作动词，管道按段逐一校验。
/// 超时：常规模式 30 秒；完全访问模式 60 秒；检测到 pip 安装时 300 秒（超时杀进程树）。
/// 输出截断到 5000 字。风险等级 High，且每次调用都必须确认、不允许"始终允许"。
/// </summary>
public class PowerShellTool : ITool
{
    private const int NormalTimeoutMs = 30_000;
    private const int FullAccessTimeoutMs = 60_000;
    private const int InstallTimeoutMs = 300_000;
    private const int MaxOutputChars = 5000;

    /// <summary>允许的命令起始动词（只读）。</summary>
    private static readonly Regex AllowedVerb = new(
        @"^\s*(Get|Test|Select|Where|Measure|Format|Group|Sort|Compare|ConvertTo|ConvertFrom|Out)-",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>禁止出现的动词（写操作 / 副作用）。</summary>
    private static readonly Regex ForbiddenVerb = new(
        @"\b(Remove|Set|New|Invoke|Start|Stop|Restart|Install|Uninstall|Enable|Disable|Set|Clear|Add|Out-File|Set-Content|Remove-Item)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string SchemaTemplate =
        """{"type":"object","properties":{"command":{"type":"string","description":"$command$"}},"required":["command"]}""";

    private readonly AiSettings _settings;
    private readonly ILogger<PowerShellTool> _logger;

    public string Name => "powershell";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate.Replace("$command$", Param("Tool_Powershell_Param_Command")));
    public ToolRiskLevel Risk => ToolRiskLevel.High;

    public PowerShellTool(AiSettings settings, ILogger<PowerShellTool> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var command = GetString(args, "command");
        if (string.IsNullOrWhiteSpace(command))
            return Task.FromResult(ToolResult.Error(Text("Tool_Powershell_Msg_MissingCommand")));

        // 完全访问模式：解除只读动词白名单与写操作动词黑名单。
        if (!_settings.FullAccessEnabled)
        {
            var validationError = Validate(command);
            if (validationError is not null)
                return Task.FromResult(ToolResult.Error(validationError));
        }

        // 依赖安装必须落在持久的用户级环境，而不是随后即被删除的临时沙箱目录。
        var packageInstall = PipInstallPolicy.HasPipInstall(command);
        var effectiveCommand = PipInstallPolicy.EnsureUserFlag(command);

        return Task.Run(() => Run(effectiveCommand, packageInstall, ct), ct);
    }

    private int GetTimeoutMs(bool packageInstall)
    {
        if (!_settings.FullAccessEnabled) return NormalTimeoutMs;
        return packageInstall ? InstallTimeoutMs : FullAccessTimeoutMs;
    }

    /// <summary>校验：每段管道都必须以只读动词开头且不含写操作动词。</summary>
    private static string? Validate(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Text("Tool_Powershell_Msg_MissingCommand");

        foreach (var raw in command.Split('|'))
        {
            var segment = raw.Trim();
            if (segment.Length == 0)
                return Text("Tool_Powershell_Msg_EmptyPipe");

            if (!AllowedVerb.IsMatch(segment))
                return Text("Tool_Powershell_Msg_BadVerb", segment);

            if (ForbiddenVerb.IsMatch(segment))
                return Text("Tool_Powershell_Msg_ForbiddenVerb", segment);
        }

        return null;
    }

    private ToolResult Run(string command, bool packageInstall, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return ToolResult.Error(Text("Tool_Msg_Cancelled"));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        // 避免 Windows 下 Python / pip 以 GBK 输出中文时崩溃。
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        _logger.LogInformation("执行 PowerShell 命令");

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 powershell.exe 失败");
            return ToolResult.Error(Text("Tool_Powershell_Msg_StartFailed", ex.Message));
        }

        using (process)
        {
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            // 用户点“取消”时立即杀掉进程树，而不是继续等满 30 秒。
            using var cancelRegistration = ct.Register(() => TryKill(process));

            var timeoutMs = GetTimeoutMs(packageInstall);
            var exited = process.WaitForExit(timeoutMs);
            if (ct.IsCancellationRequested)
            {
                TryKill(process);
                _logger.LogInformation("PowerShell 命令已取消");
                return ToolResult.Error(Format(command, SafeGet(stdOutTask), SafeGet(stdErrTask),
                    Text("Tool_Msg_Cancelled"), -1));
            }

            if (!exited)
            {
                TryKill(process);
                _logger.LogWarning("PowerShell 命令超时，已终止");
                var timeoutMessage = packageInstall
                    ? Text("Tool_Powershell_Msg_InstallTimeout", timeoutMs / 1000)
                    : Text("Tool_Powershell_Msg_Timeout", timeoutMs / 1000);
                return ToolResult.Error(Format(command, SafeGet(stdOutTask), SafeGet(stdErrTask),
                    timeoutMessage, -1));
            }

            var exitCode = process.ExitCode;
            var content = Format(command, SafeGet(stdOutTask), SafeGet(stdErrTask), null, exitCode);
            return exitCode == 0 ? ToolResult.Ok(content) : ToolResult.Error(content);
        }
    }

    private static string Format(string command, string stdOut, string stdErr, string? note, int exitCode)
    {
        var sb = new StringBuilder();
        sb.Append(Text("Tool_Powershell_Msg_ResultHeader")).AppendLine();
        sb.Append("PS> ").AppendLine(command);

        if (!string.IsNullOrWhiteSpace(stdOut))
        {
            sb.AppendLine();
            sb.AppendLine(stdOut.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(stdErr))
        {
            sb.AppendLine();
            sb.AppendLine(stdErr.TrimEnd());
        }

        if (!string.IsNullOrEmpty(note))
        {
            sb.AppendLine();
            sb.AppendLine(note);
        }
        else if (exitCode != 0)
        {
            sb.AppendLine();
            sb.Append(Text("Tool_Powershell_Msg_ExitCode", exitCode));
        }

        var text = sb.ToString().TrimEnd();
        if (text.Length > MaxOutputChars)
            text = text[..MaxOutputChars] + "\n" + Text("Tool_Powershell_Msg_Truncated");
        return text;
    }

    private static string SafeGet(Task<string> readTask)
    {
        try
        {
            return readTask.GetAwaiter().GetResult();
        }
        catch
        {
            return string.Empty;
        }
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

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.String)
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
