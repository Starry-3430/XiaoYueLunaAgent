namespace Luna.Models;

/// <summary>
/// 会话列表更新通知。携带 <see cref="SessionId"/> 时只需就地更新对应项（标题 / 置顶），
/// 为空表示需要整体刷新列表。用于避免每次通知都清空重建列表。
/// </summary>
public sealed class SessionUpdateMessage
{
    public SessionUpdateMessage(string? sessionId = null, string? title = null)
    {
        SessionId = sessionId;
        Title = title;
    }

    /// <summary>发生变化的会话 Id；为空表示整体刷新。</summary>
    public string? SessionId { get; }

    /// <summary>新标题（仅在标题生成后携带）；为空表示标题不变。</summary>
    public string? Title { get; }
}
