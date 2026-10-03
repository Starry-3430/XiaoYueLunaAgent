using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>
/// 按文件名搜索文件。优先使用 Windows 搜索索引（免安装、免管理员），
/// 未命中或索引不可用时回退到全盘目录扫描。
/// 风险 Medium：可返回任意位置的文件路径，执行前需要用户确认。
/// 限制：递归深度、结果数量与总超时。
/// </summary>
public class SearchFilesTool : ITool
{
    private const int DefaultMaxResults = 30;
    private const int HardMaxResults = 100;
    private const int MaxDepth = 12;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private static readonly char[] WildcardChars = ['*', '?'];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"query":{"type":"string","description":"文件名关键字，支持 * 和 ? 通配符"},"path":{"type":"string","description":"可选，限定搜索目录。支持别名 desktop/documents/downloads/pictures/music/videos/home（含中文），或绝对路径；留空则先搜常用目录再搜全部本地磁盘"},"max_results":{"type":"integer","description":"返回条数上限，默认 30，最大 100"}},"required":["query"]}""");

    private readonly WindowsSearchService _windowsSearch;
    private readonly ILogger<SearchFilesTool> _logger;

    public string Name => "search_files";
    public string DisplayName => "文件搜索";
    public string Description => "按文件名搜索文件，可指定目录（如桌面）。优先使用 Windows 搜索索引，未命中时回退目录扫描";
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Medium;

    public SearchFilesTool(WindowsSearchService windowsSearch, ILogger<SearchFilesTool> logger)
    {
        _windowsSearch = windowsSearch;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Error("缺少参数 query。");

        var path = GetString(args, "path");

        var maxResults = DefaultMaxResults;
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("max_results", out var m) && m.TryGetInt32(out var n))
            maxResults = Math.Clamp(n, 1, HardMaxResults);

        return await Task.Run(() => RunSearch(query, path, maxResults, ct), ct);
    }

    private ToolResult RunSearch(string query, string path, int maxResults, CancellationToken ct)
    {
        // 指定了目录：只在该目录内扫描（快速、精确，无需知道用户名即可用别名）
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!UserEnvironment.TryResolveFolder(path, out var dir) || !Directory.Exists(dir))
                return ToolResult.Error($"搜索目录不存在：{path}");

            _logger.LogInformation("文件搜索（指定目录 {Dir}）：{Query}", dir, query);
            return SearchByEnumeration(query, [dir], maxResults, ct);
        }

        // 1) 未指定目录：优先 Windows 搜索索引
        if (_windowsSearch.TryQuery(query, maxResults, out var indexed, out _))
        {
            var hits = indexed
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .ToList();

            if (hits.Count > 0)
            {
                _logger.LogInformation("文件搜索（索引）命中 {Count} 条：{Query}", hits.Count, query);
                return FormatResult(hits, "Windows 搜索索引");
            }

            _logger.LogInformation("Windows 搜索索引无命中，回退到目录扫描：{Query}", query);
        }

        // 2) 回退目录扫描：先常用目录（桌面/文档/下载…），再全部本地磁盘
        var roots = UserEnvironment.CommonFolders
            .Where(Directory.Exists)
            .Concat(EnumerateSearchRoots())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return SearchByEnumeration(query, roots, maxResults, ct);
    }

    private ToolResult SearchByEnumeration(string query, IEnumerable<string> roots, int maxResults, CancellationToken ct)
    {
        Predicate<string> match;
        try
        {
            match = BuildMatcher(query);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"无效的搜索表达式：{ex.Message}");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
            MaxRecursionDepth = MaxDepth,
        };

        var results = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        var timedOut = false;

        foreach (var root in roots)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", options))
                {
                    if (ct.IsCancellationRequested) break;

                    if (stopwatch.Elapsed > Timeout)
                    {
                        timedOut = true;
                        break;
                    }

                    if (match(Path.GetFileName(file)))
                    {
                        results.Add(file);
                        if (results.Count >= maxResults)
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "搜索目录失败：{Root}", root);
            }

            if (timedOut || results.Count >= maxResults) break;
        }

        _logger.LogInformation("文件搜索（目录扫描）命中 {Count} 条：{Query}", results.Count, query);

        if (results.Count == 0)
            return ToolResult.Ok(timedOut ? "未找到匹配的文件（搜索超时，结果可能不完整）。" : "未找到匹配的文件。");

        return FormatResult(results, null, timedOut || results.Count >= maxResults);
    }

    private static ToolResult FormatResult(List<string> results, string? source, bool truncated = false)
    {
        var sb = new StringBuilder();
        if (source is not null)
            sb.Append("来源：").Append(source).AppendLine();

        foreach (var file in results)
            sb.AppendLine(file);

        if (truncated)
            sb.AppendLine("…（结果已截断，请缩小搜索范围）");

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    /// <summary>全盘搜索根目录：所有就绪的本地固定磁盘。</summary>
    private static IEnumerable<string> EnumerateSearchRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
            yield return drive.RootDirectory.FullName;
        }
    }

    private static Predicate<string> BuildMatcher(string query)
    {
        if (query.IndexOfAny(WildcardChars) >= 0)
        {
            var pattern = "^" + Regex.Escape(query)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return name => regex.IsMatch(name);
        }

        return name => name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
