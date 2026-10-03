using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Dapper;
using Luna.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Luna.Services;

/// <summary>
/// 日记生成：把某个逻辑日（本地时间 -4h）的轮次摘要聚合成一篇日记。
/// 触发点：启动、每天 4:00 后（15 分钟轮询一次）、系统唤醒、用户发消息、打开日记视图。
/// 只处理「早于当前逻辑日」的日期，以及被显式标记为 Pending/Failed 的日期；
/// 已 Done / NoData 的日期跳过。多天积压时串行处理。
/// </summary>
public class DiaryService
{
    private const string StatusPending = "Pending";
    private const string StatusDone = "Done";
    private const string StatusFailed = "Failed";
    private const string StatusNoData = "NoData";

    private readonly IAiService _aiService;
    private readonly DatabaseInitializer _db;
    private readonly AiSettings _settings;
    private readonly ILogger<DiaryService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _timer;
    private DateTime _lastScheduledRunLocal = DateTime.MinValue;

    public DiaryService(IAiService aiService, DatabaseInitializer db, AiSettings settings,
        ILogger<DiaryService> logger)
    {
        _aiService = aiService;
        _db = db;
        _settings = settings;
        _logger = logger;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _timer.Tick += (_, _) => MaybeRunScheduled();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>启动服务：立即检查一次并开启 15 分钟轮询。</summary>
    public void Start()
    {
        _timer.Start();
        Trigger();
        _logger.LogInformation("日记服务已启动");
    }

    /// <summary>外部触发一次检查（后台执行，不阻塞）。</summary>
    public void Trigger() => _ = Task.Run(TryRunDiaryCheckAsync);

    private void MaybeRunScheduled()
    {
        var now = DateTime.Now;
        var todayBoundary = now.Date.AddHours(4); // 今日 4:00
        if (now < todayBoundary) return;                 // 还没过 4:00
        if (_lastScheduledRunLocal >= todayBoundary) return; // 今天 4:00 之后已跑过

        _lastScheduledRunLocal = now;
        Trigger();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Trigger();
    }

    /// <summary>检查并生成所有需要处理的逻辑日。串行执行，不并发。</summary>
    public async Task TryRunDiaryCheckAsync()
    {
        if (!await _gate.WaitAsync(0))
            return; // 已有一次检查在运行

        try
        {
            var dates = await GetCandidateDatesAsync();
            if (dates.Count == 0) return;

            foreach (var date in dates)
            {
                try
                {
                    await ProcessLogicalDateAsync(date);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "处理逻辑日 {Date} 失败", date);
                    await SetStatusAsync(date, StatusFailed, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "日记检查失败");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ===== 候选逻辑日 =====

    private async Task<List<string>> GetCandidateDatesAsync()
    {
        var today = LogicalDate.Now();
        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<string>(
            @"SELECT LogicalDate FROM TurnSummaries WHERE LogicalDate < @Today GROUP BY LogicalDate
              UNION
              SELECT d.LogicalDate FROM DiaryDays d
              WHERE d.Status IN ('Pending','Failed')
                AND EXISTS (SELECT 1 FROM TurnSummaries t WHERE t.LogicalDate = d.LogicalDate)
              ORDER BY LogicalDate",
            new { Today = today });
        return rows.ToList();
    }

    private async Task ProcessLogicalDateAsync(string date)
    {
        var status = await GetStatusAsync(date);
        if (status is StatusDone or StatusNoData)
            return;

        // 快照该逻辑日的轮次摘要，避免生成过程中有新数据进入
        var summaries = await GetSummariesAsync(date);
        if (summaries.Count == 0)
        {
            await SetStatusAsync(date, StatusNoData, null);
            return;
        }

        if (status is null)
            await SetStatusAsync(date, StatusPending, null);

        await GenerateDiaryAsync(date, summaries);
    }

    // ===== 生成 =====

    private async Task GenerateDiaryAsync(string date, List<TurnSummary> summaries)
    {
        var aggregated = Aggregate(summaries);
        if (string.IsNullOrWhiteSpace(aggregated))
        {
            await SetStatusAsync(date, StatusNoData, null);
            return;
        }

        var userName = string.IsNullOrWhiteSpace(_settings.UserName) ? "用户" : _settings.UserName;
        var nickname = string.IsNullOrWhiteSpace(_settings.Nickname) ? "Luna" : _settings.Nickname;

        var prompt = DefaultPrompts.DiaryPrompt
            .Replace("{nickname}", nickname)
            .Replace("{user}", userName)
            .Replace("{date}", date)
            .Replace("{summary}", aggregated);

        string content;
        try
        {
            // 非流式调用，避免半截日记被误判
            content = await _aiService.ChatAsync(
                new[] { new ChatMessage { Role = "user", Content = prompt } },
                new AiRequestOptions
                {
                    Temperature = 0.7,
                    MaxTokens = 2048,
                    IncludeTools = false,
                    IncludeSystemPrompt = false,
                    IncludeUserEnvironment = false,
                    DeepThinking = false,
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成逻辑日 {Date} 日记失败", date);
            await SetStatusAsync(date, StatusFailed, ex.Message);
            return;
        }

        content = content.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            await SetStatusAsync(date, StatusFailed, "模型返回空内容");
            return;
        }

        var sessionIds = string.Join(",", summaries.Select(s => s.SessionId).Distinct());
        var turnIds = string.Join(",", summaries.Select(s => s.TurnId).Distinct());
        var now = DateTime.UtcNow;

        var entry = new DiaryEntry
        {
            LogicalDate = date,
            Content = content,
            Model = _settings.Model,
            SourceSessionIds = sessionIds,
            SourceTurnIds = turnIds,
            TokenCount = null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            @"INSERT INTO DiaryEntries
                  (LogicalDate, Content, Model, SourceSessionIds, SourceTurnIds, TokenCount, CreatedAtUtc, UpdatedAtUtc)
              VALUES
                  (@LogicalDate, @Content, @Model, @SourceSessionIds, @SourceTurnIds, @TokenCount, @CreatedAtUtc, @UpdatedAtUtc)
              ON CONFLICT(LogicalDate) DO UPDATE SET
                  Content          = excluded.Content,
                  Model            = excluded.Model,
                  SourceSessionIds = excluded.SourceSessionIds,
                  SourceTurnIds    = excluded.SourceTurnIds,
                  TokenCount       = excluded.TokenCount,
                  UpdatedAtUtc     = excluded.UpdatedAtUtc",
            entry, tx);

        await conn.ExecuteAsync(
            @"INSERT INTO DiaryDays (LogicalDate, Status, LastError, UpdatedAtUtc)
              VALUES (@LogicalDate, 'Done', NULL, @Now)
              ON CONFLICT(LogicalDate) DO UPDATE SET
                  Status = 'Done', LastError = NULL, UpdatedAtUtc = @Now",
            new { LogicalDate = date, Now = now }, tx);

        tx.Commit();

        _logger.LogInformation("逻辑日 {Date} 日记已生成，长度 {Length}", date, content.Length);
    }

    // ===== 本地聚合 =====

    private static string Aggregate(List<TurnSummary> summaries)
    {
        var sb = new StringBuilder();
        var sessionNo = 0;

        foreach (var group in summaries.GroupBy(s => s.SessionId))
        {
            sessionNo++;
            var points = new List<string>();
            var topics = new List<string>();
            var files = new List<string>();
            string? visual = null;

            foreach (var s in group.OrderBy(x => x.TurnIndex))
            {
                ParseSummary(s.SummaryJson, points, files, topics, ref visual);
            }

            sb.AppendLine($"【会话 {sessionNo}】");
            if (points.Count > 0)
            {
                sb.AppendLine("要点：");
                foreach (var p in points.Distinct())
                    sb.AppendLine("- " + p);
            }
            if (topics.Count > 0)
                sb.AppendLine("主题：" + string.Join("、", topics.Distinct()));
            if (files.Count > 0)
                sb.AppendLine("涉及：" + string.Join("、", files.Distinct()));
            if (!string.IsNullOrWhiteSpace(visual))
                sb.AppendLine("视觉：" + visual);
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private static void ParseSummary(string json, List<string> points, List<string> files,
        List<string> topics, ref string? visual)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            foreach (var p in ReadStringArray(root, "keyPoints"))
                points.Add(p);

            foreach (var t in ReadStringArray(root, "topics"))
                topics.Add(t);

            if (TryGetProperty(root, "fileRefs", out var fileRefs) && fileRefs.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fileRefs.EnumerateArray())
                {
                    var name = f.ValueKind == JsonValueKind.Object && TryGetProperty(f, "name", out var n)
                        ? n.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name))
                        files.Add(name);
                }
            }

            if (visual is null && TryGetProperty(root, "visualDesc", out var v) && v.ValueKind == JsonValueKind.String)
            {
                var text = v.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text) && text != "无")
                    visual = text;
            }
        }
        catch (JsonException)
        {
            // 摘要不是合法 JSON，忽略
        }
    }

    private static IEnumerable<string> ReadStringArray(JsonElement obj, string name)
    {
        if (!TryGetProperty(obj, name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var text = item.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    // ===== DB 辅助 =====

    private async Task<List<TurnSummary>> GetSummariesAsync(string date)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        var rows = await conn.QueryAsync<TurnSummary>(
            "SELECT * FROM TurnSummaries WHERE LogicalDate = @Date ORDER BY SessionId, TurnIndex",
            new { Date = date });
        return rows.ToList();
    }

    private async Task<string?> GetStatusAsync(string date)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        return await conn.QueryFirstOrDefaultAsync<string?>(
            "SELECT Status FROM DiaryDays WHERE LogicalDate = @Date", new { Date = date });
    }

    private async Task SetStatusAsync(string date, string status, string? lastError)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            @"INSERT INTO DiaryDays (LogicalDate, Status, LastError, UpdatedAtUtc)
              VALUES (@Date, @Status, @LastError, @Now)
              ON CONFLICT(LogicalDate) DO UPDATE SET
                  Status = @Status, LastError = @LastError, UpdatedAtUtc = @Now",
            new { Date = date, Status = status, LastError = lastError, Now = DateTime.UtcNow });
    }

    // ===== 读取 / 检索 / 重试（供日记本 UI 使用） =====

    /// <summary>返回从安装日（最早记录）到当前逻辑日的所有日期视图，缺记录的日子为 NoData。</summary>
    public async Task<List<DiaryDayView>> GetDaysAsync()
    {
        var today = LogicalDate.Now();
        var earliest = await GetEarliestLogicalDateAsync() ?? today;

        if (!DateOnly.TryParse(earliest, out var start)) start = DateOnly.Parse(today);
        if (!DateOnly.TryParse(today, out var end)) end = start;

        var minStart = end.AddDays(-366); // 上限一年，避免异常数据导致过长
        if (start < minStart) start = minStart;
        if (start > end) start = end;

        var records = new Dictionary<string, DiaryDayView>(StringComparer.Ordinal);
        using (var conn = new SqliteConnection(_db.ConnectionString))
        {
            var rows = await conn.QueryAsync<DiaryDayView>(
                @"SELECT d.LogicalDate AS LogicalDate, d.Status AS Status, d.LastError AS LastError, e.Content AS Content
                  FROM DiaryDays d
                  LEFT JOIN DiaryEntries e ON e.LogicalDate = d.LogicalDate");
            foreach (var r in rows)
                records[r.LogicalDate] = r;
        }

        var result = new List<DiaryDayView>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var key = d.ToString("yyyy-MM-dd");
            if (records.TryGetValue(key, out var rec))
            {
                rec.IsToday = key == today;
                // 当天若尚未生成，按“还在记录”展示
                if (rec.IsToday && rec.Status is not StatusDone)
                    rec.Status = StatusPending;
                result.Add(rec);
            }
            else
            {
                result.Add(new DiaryDayView
                {
                    LogicalDate = key,
                    Status = key == today ? StatusPending : StatusNoData,
                    IsToday = key == today,
                });
            }
        }

        return result;
    }

    private async Task<string?> GetEarliestLogicalDateAsync()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        return await conn.ExecuteScalarAsync<string?>(
            @"SELECT MIN(LogicalDate) FROM (
                  SELECT LogicalDate FROM Messages
                  UNION ALL SELECT LogicalDate FROM TurnSummaries
                  UNION ALL SELECT LogicalDate FROM DiaryDays
              )");
    }

    /// <summary>强制重新生成某个逻辑日的日记（用于失败卡片的重试）。</summary>
    public async Task RetryAsync(string date)
    {
        if (!await _gate.WaitAsync(0))
            return;

        try
        {
            await SetStatusAsync(date, StatusPending, null);
            var summaries = await GetSummariesAsync(date);
            if (summaries.Count == 0)
            {
                await SetStatusAsync(date, StatusNoData, null);
                return;
            }
            await GenerateDiaryAsync(date, summaries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重试逻辑日 {Date} 日记失败", date);
            await SetStatusAsync(date, StatusFailed, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>用 DiaryFts 做全文检索，返回命中的逻辑日（按日期倒序）。</summary>
    public async Task<List<string>> SearchAsync(string query)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0) return [];

        using var conn = new SqliteConnection(_db.ConnectionString);

        // trigram 分词要求 >= 3 字符，短词或无命中时回退 LIKE
        try
        {
            var rows = (await conn.QueryAsync<string>(
                "SELECT DISTINCT LogicalDate FROM DiaryFts WHERE Content MATCH @Q ORDER BY LogicalDate DESC",
                new { Q = query })).ToList();
            if (rows.Count > 0)
                return rows;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FTS 检索失败，回退 LIKE");
        }

        var fallback = await conn.QueryAsync<string>(
            "SELECT LogicalDate FROM DiaryEntries WHERE Content LIKE '%' || @Q || '%' ORDER BY LogicalDate DESC",
            new { Q = query });
        return fallback.ToList();
    }
}
