using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"action":{"type":"string","description":"git 子命令","enum":["status","diff","log","show","branch","rev-parse","add","commit"]},"args":{"type":"array","items":{"type":"string"},"description":"传给子命令的额外参数，例如 [\"--stat\"]、[\"HEAD~3\"]、[\"newfile.txt\"]"},"message":{"type":"string","description":"提交信息，action=commit 时必填"},"path":{"type":"string","description":"仓库路径，必须在授权目录内；留空则使用工作目录"}},"required":["action"]}""");

    private readonly ToolPermissionService _permission;
    private readonly ILogger<GitTool> _logger;

    public string Name => "git";
    public string DisplayName => "Git";
    public string Description => "在仓库中执行 Git 操作：只读的 status/diff/log/show/branch/rev-parse，以及需确认的 add/commit";
    public JsonElement ParametersSchema => Schema;
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
            return ToolResult.Error("缺少参数 action。");

        var isWrite = AllowedWriteActions.Contains(action);
        if (!AllowedActions.Contains(action) && !isWrite)
            return ToolResult.Error($"不允许的 git 子命令：{action}。仅支持：status/diff/log/show/branch/rev-parse，以及需确认的 add/commit。");

        if (!TryGetArgs(args, out var extraArgs, out var argsError))
            return ToolResult.Error(argsError);

        var argError = ValidateArgs(action, extraArgs);
        if (argError is not null)
            return ToolResult.Error(argError);

        var message = GetString(args, "message");
        if (action == "commit" && string.IsNullOrWhiteSpace(message))
            return ToolResult.Error("commit 需要提供参数 message。");

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
                this, $"$ git {commandText}\n该操作会修改仓库内容，确定执行？");
            if (decision == ToolPermissionDecision.Deny)
            {
                _logger.LogInformation("用户拒绝执行 git {Action}", action);
                return ToolResult.Error("用户拒绝执行。");
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
            return ToolResult.Error($"无法启动 git，请确认已安装并加入 PATH：{ex.Message}");
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
                timedOutMessage = $"git 执行超时（{TimeoutSeconds} 秒），已终止。";
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
        sb.Append("执行结果：").AppendLine();
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
            sb.Append($"(git 退出码 {exitCode})");
        }

        var text = sb.ToString().TrimEnd();
        if (text.Length > MaxOutputChars)
            text = text[..MaxOutputChars] + "\n…（输出已截断）";
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
                return $"参数被安全策略禁止：{arg}";
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
            error = "参数 args 必须是字符串数组。";
            return false;
        }

        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = "参数 args 必须是字符串数组。";
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
