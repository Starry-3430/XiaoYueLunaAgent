using System.IO;

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

    /// <summary>允许读取的扩展名（文本 / 代码类型）。</summary>
    public static HashSet<string> AllowedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".json", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf",
        ".csv", ".tsv", ".log",
        ".cs", ".vb", ".py", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".java", ".kt", ".kts",
        ".c", ".cpp", ".cc", ".h", ".hpp", ".go", ".rs", ".rb", ".php", ".lua", ".r", ".swift", ".scala",
        ".html", ".htm", ".css", ".scss", ".less", ".sql",
        ".sh", ".bat", ".cmd", ".ps1", ".psm1", ".gradle", ".dockerfile",
    };

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
    /// 解析并校验一个已存在的可读文件。可访问任意磁盘位置，仅限定扩展名与大小；
    /// 相对路径基于 <see cref="Root"/>。
    /// </summary>
    public static bool TryResolveExisting(string input, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "路径为空。";
            return false;
        }

        try
        {
            var candidate = Path.IsPathRooted(input) ? input : Path.Combine(Root, input);
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex)
        {
            error = $"无效路径：{ex.Message}";
            return false;
        }

        var extension = Path.GetExtension(fullPath);
        if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
        {
            error = string.IsNullOrEmpty(extension)
                ? "文件没有扩展名，已被安全策略拒绝。"
                : $"不允许的文件类型：{extension}";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = $"文件不存在：{fullPath}";
            return false;
        }

        long length;
        try
        {
            length = new FileInfo(fullPath).Length;
        }
        catch (Exception ex)
        {
            error = $"无法访问文件：{ex.Message}";
            return false;
        }

        if (length > MaxBytes)
        {
            error = $"文件过大（{length / 1024} KB），超过上限 {MaxBytes / 1024} KB。";
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
            error = $"无效路径：{ex.Message}";
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            error = $"目录不存在：{fullPath}";
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
