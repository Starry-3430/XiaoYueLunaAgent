using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Luna.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

/// <summary>
/// 轮次摘要：把单个对话轮次（用户文本 / AI 文本 / 工具调用与结果）按规则裁剪后交给模型，
/// 生成结构化 JSON 并写入 TurnSummaries，供后续生成日记使用。
/// </summary>
public class TurnSummaryService
{
    private readonly IAiService _aiService;
    private readonly MessageRepository _messageRepo;
    private readonly DatabaseInitializer _db;
    private readonly AiSettings _settings;
    private readonly ILogger<TurnSummaryService> _logger;

    private const int MaxToolArgumentsLength = 200;

    private static readonly Regex CodeBlockRegex = new(
        @"```([^\r\n`]*)\r?\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BlankLineRegex = new(
        @"\r?\n[ \t]*\r?\n",
        RegexOptions.Compiled);

    public TurnSummaryService(IAiService aiService, MessageRepository messageRepo,
        DatabaseInitializer db, AiSettings settings, ILogger<TurnSummaryService> logger)
    {
        _aiService = aiService;
        _messageRepo = messageRepo;
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>在后台异步生成摘要；异常只记录日志，不影响调用方。</summary>
    public void Trigger(string sessionId, string turnId, string logicalDate)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await GenerateAsync(sessionId, turnId, logicalDate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "轮次摘要生成失败：Session={SessionId} Turn={TurnId}", sessionId, turnId);
            }
        });
    }

    public async Task GenerateAsync(string sessionId, string turnId, string logicalDate)
    {
        var messages = await _messageRepo.GetByTurnAsync(turnId);
        if (messages.Count == 0)
        {
            _logger.LogDebug("轮次 {TurnId} 没有消息，跳过摘要", turnId);
            return;
        }

        var conversation = BuildConversationText(messages);
        if (string.IsNullOrWhiteSpace(conversation))
        {
            _logger.LogDebug("轮次 {TurnId} 裁剪后为空，跳过摘要", turnId);
            return;
        }

        var userName = string.IsNullOrWhiteSpace(_settings.UserName) ? "用户" : _settings.UserName;
        var prompt = DefaultPrompts.TurnSummaryPrompt
            .Replace("{user}", userName)
            .Replace("{在这里插入对话内容}", conversation);

        var raw = await _aiService.ChatAsync(
            new[] { new ChatMessage { Role = "user", Content = prompt } },
            new AiRequestOptions
            {
                Temperature = 0,
                MaxTokens = 2048,
                IncludeTools = false,
                IncludeSystemPrompt = false,
                DeepThinking = false,
                ResponseFormat = "json_object",
            });

        var summaryJson = CleanJson(raw);
        if (!IsValidJson(summaryJson))
            _logger.LogWarning("轮次 {TurnId} 摘要不是合法 JSON，仍按原文保存", turnId);

        var turnIndex = await GetTurnIndexAsync(sessionId, messages[0].Id);

        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            @"INSERT INTO TurnSummaries
                  (TurnId, SessionId, TurnIndex, LogicalDate, SummaryJson, Model, TokenCount, CreatedAtUtc)
              VALUES
                  (@TurnId, @SessionId, @TurnIndex, @LogicalDate, @SummaryJson, @Model, @TokenCount, @CreatedAtUtc)
              ON CONFLICT(TurnId) DO UPDATE SET
                  SessionId   = excluded.SessionId,
                  TurnIndex   = excluded.TurnIndex,
                  LogicalDate = excluded.LogicalDate,
                  SummaryJson = excluded.SummaryJson,
                  Model       = excluded.Model,
                  TokenCount  = excluded.TokenCount",
            new
            {
                TurnId = turnId,
                SessionId = sessionId,
                TurnIndex = turnIndex,
                LogicalDate = logicalDate,
                SummaryJson = summaryJson,
                Model = _settings.Model,
                TokenCount = (int?)null,
                CreatedAtUtc = DateTime.UtcNow.ToString("O"),
            });

        _logger.LogInformation("轮次摘要已生成：Turn={TurnId} 长度={Length}", turnId, summaryJson.Length);
    }

    /// <summary>删除因重写 / 删消息而失去全部消息的轮次摘要。</summary>
    public async Task DeleteOrphanedAsync(string sessionId)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.ExecuteAsync(
            @"DELETE FROM TurnSummaries
              WHERE SessionId = @SessionId
                AND TurnId NOT IN (SELECT DISTINCT TurnId FROM Messages WHERE SessionId = @SessionId)",
            new { SessionId = sessionId });
    }

    private async Task<int> GetTurnIndexAsync(string sessionId, long firstMessageId)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        return await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(DISTINCT TurnId) FROM Messages
              WHERE SessionId = @SessionId AND Id <= @FirstId",
            new { SessionId = sessionId, FirstId = firstMessageId });
    }

    // ===== 规则裁剪 =====

    private static string BuildConversationText(IReadOnlyList<Message> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "user":
                    // 用户文本全保留
                    AppendContentLine(sb, "[用户] ", m.Content);
                    break;

                case "assistant":
                    var assistantText = BuildAssistantText(m.Content);
                    if (assistantText.Length > 0)
                        AppendContentLine(sb, "[助手] ", assistantText);
                    AppendToolCalls(sb, m.ToolCallsJson);
                    break;

                case "tool":
                    // 工具结果整段替换
                    sb.AppendLine("[工具结果] [输出已省略，退出码 0]");
                    break;
            }

            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private static void AppendContentLine(StringBuilder sb, string prefix, string content)
    {
        var text = content.Trim();
        if (text.Length == 0) return;
        sb.Append(prefix).AppendLine(text);
    }

    /// <summary>AI 文本保留首段 + 尾段，并压缩其中的代码块。</summary>
    private static string BuildAssistantText(string content)
    {
        var text = content.Trim();
        if (text.Length == 0) return string.Empty;

        var paragraphs = BlankLineRegex.Split(text)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (paragraphs.Count > 2)
            text = paragraphs[0] + "\n\n…\n\n" + paragraphs[^1];
        else
            text = string.Join("\n\n", paragraphs);

        return CompressCodeBlocks(text).Trim();
    }

    /// <summary>代码块只保留语言标记和首行注释。</summary>
    private static string CompressCodeBlocks(string text)
    {
        return CodeBlockRegex.Replace(text, match =>
        {
            var language = match.Groups[1].Value.Trim();
            var body = match.Groups[2].Value.Replace("\r\n", "\n");
            var firstLine = body.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            var header = language.Length > 0 ? "```" + language : "```";

            return IsCommentLine(firstLine)
                ? header + "\n" + firstLine + "\n```"
                : header + "\n```";
        });
    }

    private static bool IsCommentLine(string line) =>
        line.StartsWith("//") || line.StartsWith("#") || line.StartsWith("/*") ||
        line.StartsWith("<!--") || line.StartsWith("--") || line.StartsWith(";") ||
        line.StartsWith("'");

    private static void AppendToolCalls(StringBuilder sb, string toolCallsJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallsJson)) return;

        try
        {
            using var doc = JsonDocument.Parse(toolCallsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var call in doc.RootElement.EnumerateArray())
            {
                var name = call.TryGetProperty("ToolName", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name)) name = "工具";

                var args = call.TryGetProperty("ArgumentsJson", out var a) ? a.GetString() : null;
                args = Truncate(args, MaxToolArgumentsLength);

                sb.Append("[调用工具] ").Append(name);
                if (!string.IsNullOrWhiteSpace(args))
                    sb.Append('(').Append(args).Append(')');
                sb.AppendLine();
            }
        }
        catch (JsonException)
        {
            // 忽略无法解析的工具调用记录
        }
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max] + "…";
    }

    // ===== 输出处理 =====

    private static string CleanJson(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var newline = text.IndexOf('\n');
            if (newline >= 0) text = text[(newline + 1)..];
            if (text.EndsWith("```")) text = text[..^3];
            text = text.Trim();
        }

        return text;
    }

    private static bool IsValidJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
