// 工具：执行 Git 操作（只读查询与需确认的提交等）。
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>
/// 在授权目录内的 Git 仓库执行只读操作（status/diff/log/show/branch/rev-parse），
/// add/commit 属于写操作、执行前需二次确认。禁止 push/reset --hard/clean -fd/rebase/force 等危险命令。
/// 超时 30 秒，输出截断到 5000 字。风险等级 Low：写操作由工具内单独确认。
/// </summary>
public class GitTool : ITool
{
    private const int TimeoutSeconds = 30;
    private const int MaxOutputChars = 5000;

    /// <summary>只读子命令白名单。</summary>
    private static readonly HashSet<string> AllowedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "show", "branch", "rev-parse",
    };

    /// <summary>写子命令白名单：执行前需二次确认。</summary>
    private static readonly HashSet<string> AllowedWriteActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "commit",
    };

    /// <summary>所有子命令参数中一律禁止的危险参数（force 类 / 改写历史）。</summary>
    private static readonly HashSet<string> ForbiddenArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "-f", "--force", "--hard", "--amend", "--no-verify", "--mirror",
    };

    /// <summary>branch 子命令中禁止的删除 / 移动 / 复制参数。</summary>
    private static readonly HashSet<string> ForbiddenBranchArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "-d", "-D", "--delete", "-m", "-M", "--move", "-c", "-C", "--copy",
    };

    private const string SchemaTemplate =
        """{"type":"object","properties":{"action":{"type":"string","description":"$action$","enum":["status","diff","log","show","branch","rev-parse","add","commit"]},"args":{"type":"array","items":{"type":"string"},"description":"$args$"},"message":{"type":"string","description":"$message$"},"path":{"type":"string","description":"$path$"}},"required":["action"]}""";

    private readonly ToolPermissionService _permission;
    private readonly ILogger<GitTool> _logger;

    public string Name => "git";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate
        .Replace("$action$", Param("Tool_Git_Param_Action"))
        .Replace("$args$", Param("Tool_Git_Param_Args"))
        .Replace("$message$", Param("Tool_Git_Param_Message"))
        .Replace("$path$", Param("Tool_Git_Param_Path")));
    public ToolRiskLevel Risk => ToolRiskLevel.Low;

    public GitTool(ToolPermissionService permission, ILogger<GitTool> logger)
    {
        _permission = permission;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var action = GetString(args, "action").ToLowerInvariant();
        if (string.IsNullOrEmpty(action))
            return ToolResult.Error(Text("Tool_Msg_MissingParam", "action"));

        var isWrite = AllowedWriteActions.Contains(action);
        if (!AllowedActions.Contains(action) && !isWrite)
            return ToolResult.Error(Text("Tool_Git_Msg_BadAction", action));

        if (!TryGetArgs(args, out var extraArgs, out var argsError))
            return ToolResult.Error(argsError);

        var argError = ValidateArgs(action, extraArgs);
        if (argError is not null)
            return ToolResult.Error(argError);

        var message = GetString(args, "message");
        if (action == "commit" && string.IsNullOrWhiteSpace(message))
            return ToolResult.Error(Text("Tool_Git_Msg_NeedMessage"));

        var path = GetString(args, "path");
        if (!FileSandbox.TryResolveAnyExistingDirectory(path, out var repo, out var pathError))
            return ToolResult.Error(pathError);

        var gitArgs = new List<string> { action };
        gitArgs.AddRange(extraArgs);
        if (action == "commit")
        {
            gitArgs.Add("-m");
            gitArgs.Add(message);
        }

        var commandText = string.Join(' ', gitArgs.Select(Quote));

        if (isWrite)
        {
            var decision = _permission.RequestConfirmation(
                this, Text("Tool_Git_Msg_ConfirmWrite", commandText));
            if (decision == ToolPermissionDecision.Deny)
            {
                _logger.LogInformation("用户拒绝执行 git {Action}", action);
                return ToolResult.Error(Text("Tool_Msg_UserDenied"));
            }
        }

        return await RunGitAsync(repo, gitArgs, commandText, ct);
    }

    private async Task<ToolResult> RunGitAsync(string repo, IReadOnlyList<string> gitArgs, string commandText, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in gitArgs)
            psi.ArgumentList.Add(arg);

        _logger.LogInformation("执行 git：{Command}（cwd={Repo}）", commandText, repo);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 git 失败");
            return ToolResult.Error(Text("Tool_Git_Msg_StartFailed", ex.Message));
        }

        using (process)
        {
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            int exitCode;
            string timedOutMessage = string.Empty;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (ct.IsCancellationRequested)
                    throw;
                exitCode = -1;
                timedOutMessage = Text("Tool_Git_Msg_Timeout", TimeoutSeconds);
            }

            var stdOut = await SafeReadAsync(stdOutTask);
            var stdErr = await SafeReadAsync(stdErrTask);

            var content = Format(commandText, stdOut, stdErr, timedOutMessage, exitCode);
            if (exitCode != 0)
                return ToolResult.Error(content);
            return ToolResult.Ok(content);
        }
    }

    private static string Format(string commandText, string stdOut, string stdErr, string timedOutMessage, int exitCode)
    {
        var sb = new StringBuilder();
        sb.Append(Text("Tool_Git_Msg_ResultHeader")).AppendLine();
        sb.Append("$ git ").AppendLine(commandText);

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

        if (!string.IsNullOrEmpty(timedOutMessage))
        {
            sb.AppendLine();
            sb.AppendLine(timedOutMessage);
        }
        else if (exitCode != 0)
        {
            sb.AppendLine();
            sb.Append(Text("Tool_Git_Msg_ExitCode", exitCode));
        }

        var text = sb.ToString().TrimEnd();
        if (text.Length > MaxOutputChars)
            text = text[..MaxOutputChars] + "\n" + Text("Tool_Git_Msg_Truncated");
        return text;
    }

    private static async Task<string> SafeReadAsync(Task<string> readTask)
    {
        try
        {
            return await readTask;
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

    private static string? ValidateArgs(string action, IReadOnlyList<string> args)
    {
        foreach (var raw in args)
        {
            var arg = raw.Trim();
            if (arg.Length == 0) continue;

            var lower = arg.ToLowerInvariant();
            if (lower.Contains("force") ||
                ForbiddenArgs.Contains(lower) ||
                (action == "branch" && ForbiddenBranchArgs.Contains(lower)))
                return Text("Tool_Git_Msg_ArgForbidden", arg);
        }
        return null;
    }

    /// <summary>按 shell 习惯为含空白的参数加引号，仅用于展示。</summary>
    private static string Quote(string arg)
        => arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg;

    private static bool TryGetArgs(JsonElement args, out List<string> result, out string error)
    {
        result = new List<string>();
        error = string.Empty;

        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("args", out var el) ||
            el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return true;

        if (el.ValueKind != JsonValueKind.Array)
        {
            error = Text("Tool_Git_Msg_ArgsNotArray");
            return false;
        }

        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = Text("Tool_Git_Msg_ArgsNotArray");
                return false;
            }
            result.Add(item.GetString() ?? string.Empty);
        }
        return true;
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.String)
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
