// pip 安装识别与 --user 注入：确保依赖安装到持久的用户级环境，而不是临时工作目录。
using System.Text.RegularExpressions;

namespace Luna.Services.Tools;

/// <summary>
/// 识别代码 / 命令中的 pip 安装行为，并为安装命令补上 <c>--user</c>，
/// 避免依赖被装进每次执行后即删除的临时沙箱目录。
/// </summary>
public static class PipInstallPolicy
{
    private static readonly Regex PipInstallPattern = new(
        @"\bpip3?\s+install\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SubprocessPattern = new(
        @"\bsubprocess\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>命令已显式指定安装位置时不再追加 --user。</summary>
    private static readonly Regex ExplicitLocationPattern = new(
        @"--user\b|--target\b|--prefix\b|--root\b|(?:^|\s)-t(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>是否包含 <c>pip install</c> / <c>pip3 install</c>。</summary>
    public static bool HasPipInstall(string text) => PipInstallPattern.IsMatch(text);

    /// <summary>是否可能需要安装依赖（pip 安装或 subprocess 调用）。</summary>
    public static bool IsPackageInstall(string text) =>
        HasPipInstall(text) || SubprocessPattern.IsMatch(text);

    /// <summary>给每个 pip install 命令补上 --user；已有安装位置参数时保持原样。</summary>
    public static string EnsureUserFlag(string text)
    {
        return PipInstallPattern.Replace(text, match =>
        {
            var lineEnd = text.IndexOfAny(['\r', '\n'], match.Index);
            var tail = lineEnd < 0 ? text[match.Index..] : text[match.Index..lineEnd];
            return ExplicitLocationPattern.IsMatch(tail) ? match.Value : match.Value + " --user";
        });
    }
}
