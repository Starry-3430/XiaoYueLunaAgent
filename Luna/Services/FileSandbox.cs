using System.IO;

namespace Luna.Services;

/// <summary>
/// 文件工具的沙箱限制：限定目录、限定扩展名、限定大小。
/// read_file / write_file 等工具复用这里的规则，避免访问系统敏感路径。
/// </summary>
public static class FileSandbox
{
    /// <summary>专用沙箱目录。</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Luna", "workspace");

    /// <summary>允许访问的目录白名单。</summary>
    public static IReadOnlyList<string> AllowedRoots { get; } = BuildAllowedRoots();

    /// <summary>允许读取/写入的扩展名。</summary>
    public static HashSet<string> AllowedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".json", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf",
        ".csv", ".tsv", ".log",
        ".cs", ".vb", ".py", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".java", ".kt", ".kts",
        ".c", ".cpp", ".cc", ".h", ".hpp", ".go", ".rs", ".rb", ".php", ".lua", ".r", ".swift", ".scala",
        ".html", ".htm", ".css", ".scss", ".less", ".sql",
        ".sh", ".bat", ".cmd", ".ps1", ".psm1", ".gradle", ".dockerfile",
    };

    /// <summary>单文件大小上限（256 KB）。</summary>
    public const long MaxBytes = 256 * 1024;

    private static string[] BuildAllowedRoots()
    {
        var roots = new List<string> { Root };

        void Add(Environment.SpecialFolder folder)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path))
                roots.Add(path);
        }

        Add(Environment.SpecialFolder.MyDocuments);
        Add(Environment.SpecialFolder.Desktop);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            roots.Add(Path.Combine(profile, "Downloads"));

        return roots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 解析并校验一个已存在的可读文件。相对路径基于沙箱 <see cref="Root"/>。
    /// </summary>
    public static bool TryResolveExisting(string input, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (!TryResolvePath(input, out fullPath, out error))
            return false;

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

    /// <summary>解析路径并校验扩展名与目录白名单（不要求文件必须存在）。</summary>
    public static bool TryResolvePath(string input, out string fullPath, out string error)
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

        if (!IsInsideAllowedRoots(fullPath))
        {
            error = "路径不在允许访问的目录内。";
            return false;
        }

        return true;
    }

    /// <summary>路径是否位于允许访问的目录内。</summary>
    public static bool IsAllowed(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        try
        {
            return IsInsideAllowedRoots(Path.GetFullPath(fullPath));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInsideAllowedRoots(string fullPath)
    {
        foreach (var root in AllowedRoots)
        {
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            if (fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
