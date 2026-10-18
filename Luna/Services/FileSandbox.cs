using System.IO;
using Luna.Localization;

namespace Luna.Services;

/// <summary>
/// 文件工具的安全限制。读取为全盘可访问（仍限定扩展名与大小），
/// 写入由 write_file 限定到设置中的输出目录并校验扩展名黑名单。
/// </summary>
public static class FileSandbox
{
    /// <summary>相对路径的基准工作目录。</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Luna", "workspace");

    /// <summary>写入黑名单：可执行 / 脚本 / 安装包 / 二进制类型，一律禁止写入。</summary>
    public static HashSet<string> BlockedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".ps1", ".psm1", ".cmd", ".scr", ".msi", ".com",
        ".cpl", ".sys", ".drv", ".ocx", ".pif", ".vbs", ".vbe", ".wsf", ".wsh",
        ".jse", ".hta", ".gadget", ".msc", ".lnk", ".jar", ".class",
        ".apk", ".bin", ".iso", ".img", ".dmg", ".so", ".dylib",
    };

    /// <summary>单文件大小上限（1024 KB）。</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// 解析并校验一个已存在的可读文件。可访问任意磁盘位置，只限制大小；
    /// 是否为可读文本由读取工具在读取后按内容判断。
    /// 相对路径基于 <see cref="Root"/>。
    /// </summary>
    public static bool TryResolveExisting(string input, out string fullPath, out string error,
        long maxBytes = MaxBytes)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = LocalizationService.Instance["Tool_Fs_PathEmpty"];
            return false;
        }

        try
        {
            var candidate = Path.IsPathRooted(input) ? input : Path.Combine(Root, input);
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex)
        {
            error = LocalizationService.Instance.Format("Tool_Fs_InvalidPath", ex.Message);
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = LocalizationService.Instance.Format("Tool_Fs_NotFound", fullPath);
            return false;
        }

        long length;
        try
        {
            length = new FileInfo(fullPath).Length;
        }
        catch (Exception ex)
        {
            error = LocalizationService.Instance.Format("Tool_Fs_AccessDenied", ex.Message);
            return false;
        }

        if (length > maxBytes)
        {
            error = LocalizationService.Instance.Format("Tool_Fs_TooLarge", length / 1024, maxBytes / 1024);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 解析任意一个已存在的目录。相对路径基于 <see cref="Root"/>，支持常用目录别名
    /// （desktop/documents/downloads/home 等）；留空时使用 <see cref="Root"/>。
    /// </summary>
    public static bool TryResolveAnyExistingDirectory(string? input, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        string candidate;
        if (string.IsNullOrWhiteSpace(input))
            candidate = Root;
        else if (!UserEnvironment.TryResolveFolder(input, out candidate))
            candidate = Path.Combine(Root, input);

        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex)
        {
            error = LocalizationService.Instance.Format("Tool_Fs_InvalidPath", ex.Message);
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            error = LocalizationService.Instance.Format("Tool_Fs_DirNotFound", fullPath);
            return false;
        }

        return true;
    }

    /// <summary>路径是否位于指定目录内（含目录本身）。</summary>
    public static bool IsInsideDirectory(string fullPath, string directory)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(directory))
            return false;

        try
        {
            var normalizedDir = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var target = Path.GetFullPath(fullPath);

            return target.Equals(normalizedDir, StringComparison.OrdinalIgnoreCase)
                   || target.StartsWith(normalizedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
