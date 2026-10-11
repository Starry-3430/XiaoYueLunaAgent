// 工具：输出指定目录的文件树结构。
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>
/// 获取目录的文件树结构。复用 <see cref="FileSandbox"/> 的路径规范化（支持目录别名），
/// 仅列出名称、不读取文件内容；访问范围为全量磁盘（不设授权目录限制）。
/// 限制递归深度、条目数与取消。风险等级 Low：只读、只返回名称，直接执行。
/// </summary>
public class FileTreeTool : ITool
{
    private const int DefaultDepth = 2;
    private const int MaxDepth = 6;
    private const int DefaultMaxEntries = 500;
    private const int HardMaxEntries = 2000;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"path":{"type":"string","description":"要列出目录树的目录。支持 desktop/documents/downloads/pictures/home 等别名（含中文）或绝对路径；相对路径基于工作目录；留空则使用工作目录"},"depth":{"type":"integer","description":"递归深度，默认 2，最大 6"},"max_entries":{"type":"integer","description":"返回条目上限，默认 500，最大 2000"}}}""");

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    private readonly ILogger<FileTreeTool> _logger;

    public string Name => "file_tree";
    public string DisplayName => "目录树浏览";
    public string Description => "获取指定目录的文件树结构（支持深度与条目数限制）";
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Low;

    public FileTreeTool(ILogger<FileTreeTool> logger)
    {
        _logger = logger;
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var path = GetString(args, "path");
        if (!FileSandbox.TryResolveAnyExistingDirectory(path, out var fullPath, out var error))
            return Task.FromResult(ToolResult.Error(error));

        var depth = GetInt(args, "depth", DefaultDepth, 1, MaxDepth);
        var maxEntries = GetInt(args, "max_entries", DefaultMaxEntries, 1, HardMaxEntries);

        return Task.Run(() => Build(fullPath, depth, maxEntries, ct), ct);
    }

    private ToolResult Build(string root, int maxDepth, int maxEntries, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var count = 0;
        var truncated = false;

        sb.Append("目录：").AppendLine(root);

        void Walk(string dir, string prefix, int level)
        {
            if (ct.IsCancellationRequested || truncated) return;

            List<(string Name, bool IsDir)> entries;
            try
            {
                entries = new List<(string, bool)>();
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", EnumerationOptions))
                    entries.Add((Path.GetFileName(sub), true));
                foreach (var file in Directory.EnumerateFiles(dir, "*", EnumerationOptions))
                    entries.Add((Path.GetFileName(file), false));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "枚举目录失败：{Dir}", dir);
                return;
            }

            entries.Sort(static (a, b) =>
            {
                if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            for (var i = 0; i < entries.Count; i++)
            {
                if (ct.IsCancellationRequested) return;
                if (count >= maxEntries)
                {
                    truncated = true;
                    return;
                }

                var (name, isDir) = entries[i];
                var isLast = i == entries.Count - 1;
                sb.Append(prefix)
                    .Append(isLast ? "└─ " : "├─ ")
                    .Append(name)
                    .Append(isDir ? "/" : string.Empty)
                    .AppendLine();
                count++;

                if (isDir && level < maxDepth)
                {
                    var nextPrefix = prefix + (isLast ? "   " : "│  ");
                    Walk(Path.Combine(dir, name), nextPrefix, level + 1);
                }
            }
        }

        Walk(root, string.Empty, 1);

        if (truncated)
            sb.AppendLine($"…（已达到条目上限 {maxEntries}，结果被截断）");

        _logger.LogInformation("目录树浏览：{Root}，条目 {Count}", root, count);
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }

    private static int GetInt(JsonElement args, string name, int fallback, int min, int max)
    {
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty(name, out var v) && v.TryGetInt32(out var n))
            return Math.Clamp(n, min, max);
        return fallback;
    }
}
