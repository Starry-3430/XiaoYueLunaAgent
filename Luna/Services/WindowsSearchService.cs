using System.Data.OleDb;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

/// <summary>
/// 通过 Windows 搜索索引（SYSTEMINDEX）查询文件。
/// 免安装、免管理员，依赖 Windows Search 服务已启用且目标目录已被索引。
/// </summary>
public class WindowsSearchService
{
    private const string ConnectionString =
        "Provider=Search.CollatorDSO;Extended Properties=\"Application=Windows\";";

    private readonly ILogger<WindowsSearchService> _logger;

    public WindowsSearchService(ILogger<WindowsSearchService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 查询文件名匹配的文件路径。返回 false 表示索引不可用（调用方应回退到目录扫描）。
    /// </summary>
    public bool TryQuery(string query, int maxResults, out List<string> results, out string? error)
    {
        results = [];
        error = null;

        try
        {
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandTimeout = 5; // 秒
            command.CommandText =
                $"SELECT TOP {maxResults} System.ItemPathDisplay " +
                $"FROM SYSTEMINDEX WHERE System.FileName LIKE '{BuildLikePattern(query)}'";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var path = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(path))
                    results.Add(path);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogWarning(ex, "Windows 搜索索引查询失败，将回退到目录扫描");
            return false;
        }
    }

    /// <summary>构造 Windows Search 的 LIKE 模式：无通配符时按子串匹配。</summary>
    private static string BuildLikePattern(string query)
    {
        var hasWildcard = query.IndexOfAny(['*', '?']) >= 0;

        var pattern = query
            .Replace("'", "''")
            .Replace('*', '%')
            .Replace('?', '_');

        return hasWildcard ? pattern : "%" + pattern + "%";
    }
}
