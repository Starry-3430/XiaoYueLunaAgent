using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Messaging;
using Luna.Models;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

/// <summary>
/// 会话标题：会话首轮对话完成后，用模型根据用户首条消息与 AI 回复生成简短标题，
/// 写回 Sessions.Title，并通知界面刷新当前标题与左侧列表标题。
/// </summary>
public class SessionTitleService
{
    private readonly IAiService _aiService;
    private readonly SessionRepository _sessionRepo;
    private readonly ILogger<SessionTitleService> _logger;

    // 送入模型的原文上限，避免标题生成请求过大。
    private const int MaxInputLength = 1000;
    private const int MaxTitleLength = 30;

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public SessionTitleService(IAiService aiService, SessionRepository sessionRepo,
        ILogger<SessionTitleService> logger)
    {
        _aiService = aiService;
        _sessionRepo = sessionRepo;
        _logger = logger;
    }

    /// <summary>在后台异步生成标题；异常只记录日志，不影响调用方。</summary>
    public void Trigger(string sessionId, string userText, string assistantText)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await GenerateAsync(sessionId, userText, assistantText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "会话标题生成失败：Session={SessionId}", sessionId);
            }
        });
    }

    public async Task GenerateAsync(string sessionId, string userText, string assistantText)
    {
        if (string.IsNullOrWhiteSpace(userText) && string.IsNullOrWhiteSpace(assistantText))
            return;

        var prompt = DefaultPrompts.SessionTitlePrompt
            .Replace("{user}", Truncate(userText))
            .Replace("{assistant}", Truncate(assistantText));

        var raw = await _aiService.ChatAsync(
            new[] { new ChatMessage { Role = "user", Content = prompt } },
            new AiRequestOptions
            {
                Temperature = 0,
                MaxTokens = 64,
                IncludeTools = false,
                IncludeSystemPrompt = false,
                IncludeUserEnvironment = false,
                DeepThinking = false,
            });

        var title = CleanTitle(raw);
        if (string.IsNullOrWhiteSpace(title))
        {
            _logger.LogDebug("会话 {SessionId} 标题为空，跳过写入", sessionId);
            return;
        }

        await _sessionRepo.UpdateTitleAsync(sessionId, title);
        _logger.LogInformation("会话标题已生成：Session={SessionId} Title={Title}", sessionId, title);

        // 通知界面刷新：更新当前会话标题与左侧列表
        WeakReferenceMessenger.Default.Send(new SessionUpdateMessage());
    }

    private static string Truncate(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= MaxInputLength ? text : text[..MaxInputLength];
    }

    /// <summary>清理模型输出：去掉引号、换行与结尾标点，并限制长度。</summary>
    private static string CleanTitle(string raw)
    {
        var title = (raw ?? string.Empty).Trim();
        if (title.Length == 0) return string.Empty;

        // 去掉 markdown 代码块包裹
        if (title.StartsWith("```"))
        {
            var firstNewline = title.IndexOf('\n');
            if (firstNewline >= 0) title = title[(firstNewline + 1)..];
            if (title.EndsWith("```")) title = title[..^3];
            title = title.Trim();
        }

        // 取第一行，去掉常见包裹符号
        var newline = title.IndexOfAny(['\r', '\n']);
        if (newline >= 0) title = title[..newline];

        title = title.Trim()
            .Trim('"', '\'', '"', '"', '「', '」', '『', '』', '《', '》', '：', ':', '.', '。', '!', '！', '?', '？');

        title = WhitespaceRegex.Replace(title, " ").Trim();

        if (title.Length > MaxTitleLength)
            title = title[..MaxTitleLength].TrimEnd();

        return title;
    }
}
