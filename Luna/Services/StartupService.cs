using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Luna.Services;

/// <summary>
/// 开机自启动：none（关闭）/ user（当前用户，写入注册表 Run 项）/
/// admin（以管理员身份启动，创建“登录时运行、最高权限”的计划任务）。
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Luna";
    private const string TaskName = "Luna";

    /// <summary>开机自启动时附加的命令行参数，用于让应用启动但不显示主窗口。</summary>
    public const string StartupArgument = "--startup";

    /// <summary>提权辅助进程参数：以管理员身份启动后仅执行“管理员自启动”设置，随即退出。</summary>
    public const string AdminHelperArgument = "--apply-startup-admin";

    private readonly ILogger<StartupService> _logger;

    public StartupService(ILogger<StartupService> logger)
    {
        _logger = logger;
    }

    /// <summary>当前进程可执行文件路径。</summary>
    private static string ExecutablePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("无法确定可执行文件路径。");

    /// <summary>应用指定的自启动模式，返回是否成功以及失败原因。</summary>
    public StartupApplyResult Apply(string mode)
    {
        try
        {
            switch (mode)
            {
                case "user":
                    DeleteScheduledTask();
                    EnableRegistry();
                    return StartupApplyResult.Ok();
                case "admin":
                    DisableRegistry();
                    return EnableScheduledTask();
                default:
                    DisableRegistry();
                    DeleteScheduledTask();
                    return StartupApplyResult.Ok();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置开机自启动失败：{Mode}", mode);
            return StartupApplyResult.Fail(ex.Message);
        }
    }

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>以管理员身份重新启动自身执行“管理员自启动”设置，等待完成后返回结果。</summary>
    public StartupApplyResult ApplyElevatedAdmin()
    {
        // 清除上一次的结果，避免读到旧值
        TryDeleteHelperResult();

        var psi = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = AdminHelperArgument,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return StartupApplyResult.Fail("无法启动提权进程。");

            process.WaitForExit();

            var detail = TryReadHelperResult();
            if (process.ExitCode == 0)
            {
                _logger.LogInformation("管理员自启动已通过提权进程设置成功");
                return StartupApplyResult.Ok();
            }

            _logger.LogWarning("提权进程执行失败，退出码：{Code}，原因：{Detail}", process.ExitCode, detail);
            return StartupApplyResult.Fail(string.IsNullOrWhiteSpace(detail)
                ? "提权进程未能完成设置。"
                : detail);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED：用户在 UAC 界面选择了“否”
            _logger.LogInformation("用户取消了管理员提权");
            return StartupApplyResult.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "请求管理员提权失败");
            return StartupApplyResult.Fail(ex.Message);
        }
    }

    /// <summary>提权辅助进程入口：执行“管理员自启动”设置，写入结果文件并返回退出码。</summary>
    public static int RunAdminHelper()
    {
        var result = new StartupService(NullLogger<StartupService>.Instance).Apply("admin");
        try
        {
            File.WriteAllText(HelperResultPath, result.Message);
        }
        catch
        {
            // 结果文件仅用于诊断，写入失败不影响退出码
        }

        return result.Success ? 0 : 1;
    }

    private static string HelperResultPath =>
        Path.Combine(Path.GetTempPath(), "luna-startup-admin.result");

    private static string? TryReadHelperResult()
    {
        try
        {
            return File.Exists(HelperResultPath) ? File.ReadAllText(HelperResultPath) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteHelperResult()
    {
        try
        {
            File.Delete(HelperResultPath);
        }
        catch
        {
            // ignore
        }
    }

    private void EnableRegistry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("无法写入注册表 Run 项。");
        key.SetValue(ValueName, $"\"{ExecutablePath}\" {StartupArgument}");
        _logger.LogInformation("已开启当前用户开机自启动：{Path}", ExecutablePath);
    }

    private void DisableRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) is null) return;
        key.DeleteValue(ValueName, throwOnMissingValue: false);
        _logger.LogInformation("已关闭当前用户开机自启动");
    }

    /// <summary>创建“登录时运行、最高权限”的计划任务；需要管理员权限才能成功。</summary>
    private StartupApplyResult EnableScheduledTask()
    {
        var result = RunSchtasks(
            "/Create",
            "/TN", TaskName,
            "/TR", $"\"{ExecutablePath}\" {StartupArgument}",
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/F");

        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            _logger.LogWarning("创建管理员计划任务失败：{Message}", message);
            return StartupApplyResult.Fail(message.Trim());
        }

        _logger.LogInformation("已开启管理员开机自启动（计划任务 {Task}）", TaskName);
        return StartupApplyResult.Ok();
    }

    private static void DeleteScheduledTask()
    {
        RunSchtasks("/Delete", "/TN", TaskName, "/F");
    }

    private static SchtasksResult RunSchtasks(params string[] args)
    {
        var encoding = GetConsoleEncoding();
        var psi = new ProcessStartInfo("schtasks")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null) return new SchtasksResult(-1, string.Empty, "无法启动 schtasks。");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new SchtasksResult(process.ExitCode, output, error);
    }

    /// <summary>schtasks 等控制台程序以系统 OEM 代码页输出，需按该代码页解码。</summary>
    private static Encoding GetConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private readonly record struct SchtasksResult(int ExitCode, string Output, string Error);
}

/// <summary>应用开机自启动模式的结果。</summary>
public readonly record struct StartupApplyResult(bool Success, string Message, bool Canceled = false)
{
    public static StartupApplyResult Ok() => new(true, string.Empty);

    public static StartupApplyResult Fail(string message) =>
        new(false, string.IsNullOrWhiteSpace(message) ? "未知错误。" : message);

    public static StartupApplyResult Cancelled() => new(false, "已取消授权。", true);
}
