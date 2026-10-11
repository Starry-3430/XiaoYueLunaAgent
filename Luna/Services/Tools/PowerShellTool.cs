using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>
/// 执行只读 PowerShell 命令。命令必须以只读动词开头、不得包含写操作动词，管道按段逐一校验。
/// 超时 30 秒（超时杀进程树），输出截断到 5000 字。风险等级 High，且每次调用都必须确认、不允许"始终允许"。
/// </summary>
public class PowerShellTool : ITool
{
    private const int TimeoutMs = 30_000;
    private const int MaxOutputChars = 5000;

    /// <summary>允许的命令起始动词（只读）。</summary>
    private static readonly Regex AllowedVerb = new(
        @"^\s*(Get|Test|Select|Where|Measure|Format|Group|Sort|Compare|ConvertTo|ConvertFrom|Out)-",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>禁止出现的动词（写操作 / 副作用）。</summary>
    private static readonly Regex ForbiddenVerb = new(
        @"\b(Remove|Set|New|Invoke|Start|Stop|Restart|Install|Uninstall|Enable|Disable|Set|Clear|Add|Out-File|Set-Content|Remove-Item)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"command":{"type":"string","description":"要执行的只读 PowerShell 命令。可用管道，但每个管道段都必须以 Get/Test/Select/Where/Measure/Format/Group/Sort/Compare/ConvertTo/ConvertFrom/Out- 开头，且不得包含 Remove/Set/New/Invoke/Start/Stop/Restart/Install/Uninstall/Enable/Disable/Clear/Add/Out-File/Set-Content/Remove-Item 等写操作"}},"required":["command"]}""");

    private readonly ILogger<PowerShellTool> _logger;

    public string Name => "powershell";
    public string DisplayName => "PowerShell";
    public string Description => "执行只读 PowerShell 命令（只读动词白名单 + 写操作黑名单，每次执行前确认）";
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.High;

    public PowerShellTool(ILogger<PowerShellTool> logger)
    {
        _logger = logger;
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var command = GetString(args, "command");
        var validationError = Validate(command);
        if (validationError is not null)
            return Task.FromResult(ToolResult.Error(validationError));

        return Task.Run(() => Run(command, ct), ct);
    }

    /// <summary>校验：每段管道都必须以只读动词开头且不含写操作动词。</summary>
    private static string? Validate(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "缺少参数 command。";

        foreach (var raw in command.Split('|'))
        {
            var segment = raw.Trim();
            if (segment.Length == 0)
                return "命令包含空的管道段。";

            if (!AllowedVerb.IsMatch(segment))
                return "不允许的命令：每个管道段都必须以只读动词开头（Get/Test/Select/Where/Measure/Format/Group/Sort/Compare/ConvertTo/ConvertFrom/Out-）。\n" +
                       $"问题段：{segment}";

            if (ForbiddenVerb.IsMatch(segment))
                return "不允许的命令：包含被禁止的写操作动词。\n" +
                       $"问题段：{segment}";
        }

        return null;
    }

    private ToolResult Run(string command, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return ToolResult.Error("执行被取消。");

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

        _logger.LogInformation("执行 PowerShell 命令：{Command}", command);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 powershell.exe 失败");
            return ToolResult.Error($"无法启动 powershell.exe：{ex.Message}");
        }

        using (process)
        {
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeoutMs))
            {
                TryKill(process);
                _logger.LogWarning("PowerShell 命令超时，已终止：{Command}", command);
                return ToolResult.Error(Format(command, SafeGet(stdOutTask), SafeGet(stdErrTask),
                    $"PowerShell 执行超时（{TimeoutMs / 1000} 秒），已终止进程树。", -1));
            }

            var exitCode = process.ExitCode;
            var content = Format(command, SafeGet(stdOutTask), SafeGet(stdErrTask), null, exitCode);
            return exitCode == 0 ? ToolResult.Ok(content) : ToolResult.Error(content);
        }
    }

    private static string Format(string command, string stdOut, string stdErr, string? note, int exitCode)
    {
        var sb = new StringBuilder();
        sb.Append("执行结果：").AppendLine();
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
            sb.Append($"(退出码 {exitCode})");
        }

        var text = sb.ToString().TrimEnd();
        if (text.Length > MaxOutputChars)
            text = text[..MaxOutputChars] + "\n…（输出已截断）";
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
