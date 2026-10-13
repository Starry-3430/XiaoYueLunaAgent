using System.Windows.Threading;
using Luna.Models;

namespace Luna.Controls;

/// <summary>
/// 会话消息的延迟渲染调度器：把 Markdown 渲染从视图 Loaded 的同步路径挪到调度泵上，
/// 按“最新 → 最旧”的顺序逐条渲染，并在每条之间让出 UI 线程。
/// 这样打开会话时优先呈现最新的底部内容，也不会因为一次性同步解析全部消息而卡死。
/// 每个窗口一个实例，所有方法都必须在 UI 线程调用。
/// </summary>
internal sealed class MessageRenderScheduler
{
    private readonly Func<bool> _isActive;
    private readonly Func<ChatMessage, int> _indexOf;
    private readonly Action _afterEachRender;

    private readonly Dictionary<ChatMessage, Entry> _entries = new();
    private readonly HashSet<ChatMessage> _pending = new();
    private bool _pumpActive;
    private int _epoch;

    private sealed record Entry(WpfMarkdownViewer.Controls.MarkdownDocumentView Viewer, Action Render);

    public MessageRenderScheduler(Func<bool> isActive,
        Func<ChatMessage, int> indexOf, Action afterEachRender)
    {
        _isActive = isActive;
        _indexOf = indexOf;
        _afterEachRender = afterEachRender;
    }

    /// <summary>绑定集合被替换（切换会话）时调用：使进行中的渲染失效并清空待渲染队列。</summary>
    public void Reset()
    {
        _epoch++;
        _pending.Clear();
        _entries.Clear();
    }

    public void Register(ChatMessage message, WpfMarkdownViewer.Controls.MarkdownDocumentView viewer, Action render)
    {
        // 新 viewer 绑定到消息上：之前的渲染进度对它无意义，必须重置。
        // StreamingState 挂在 ChatMessage 上、跨 viewer 生命周期复用；若不清零，
        // 切回会话时旧进度会让 PumpAsync 误判“已渲染完”而跳过，新 viewer 就是空的。
        message.StreamingState.RenderedLength = 0;
        message.StreamingState.Pending = false;

        // 新 viewer 里还没有任何内容：标记为未渲染，模板据此显示加载占位骨架。
        message.IsBodyRendered = false;

        _entries[message] = new Entry(viewer, render);
    }

    public void Unregister(ChatMessage message, WpfMarkdownViewer.Controls.MarkdownDocumentView viewer)
    {
        // 仅当登记的确实是这个视图时才移除，避免旧视图的延迟 Unloaded 误删新视图的映射与待渲染队列
        if (_entries.TryGetValue(message, out var entry) && ReferenceEquals(entry.Viewer, viewer))
        {
            _entries.Remove(message);
            _pending.Remove(message);
        }
    }

    /// <summary>请求（重新）渲染某条消息；实际渲染由调度泵按优先级推进。</summary>
    public void Request(ChatMessage message)
    {
        if (!_entries.ContainsKey(message)) return;
        if (string.IsNullOrEmpty(message.Content) && !message.IsStreaming) return;
        _pending.Add(message);
        EnsurePump();
    }

    /// <summary>会话变为可见（如展开、切回聊天）时调用，唤醒因不可见而暂停的渲染泵。</summary>
    public void Wake() => EnsurePump();

    private void EnsurePump()
    {
        if (_pumpActive || _pending.Count == 0 || !_isActive()) return;
        _pumpActive = true;
        _ = PumpAsync(_epoch);
    }

    private async Task PumpAsync(int epoch)
    {
        try
        {
            while (epoch == _epoch && _isActive() && _pending.Count > 0)
            {
                ChatMessage? next = null;
                var bestIndex = -1;
                List<ChatMessage>? stale = null;

                foreach (var candidate in _pending)
                {
                    var index = _indexOf(candidate);
                    if (index < 0)
                    {
                        (stale ??= new List<ChatMessage>()).Add(candidate);
                        continue;
                    }

                    if (index > bestIndex)
                    {
                        bestIndex = index;
                        next = candidate;
                    }
                }

                if (stale is not null)
                    foreach (var dead in stale) _pending.Remove(dead);

                if (next is null) break;

                _pending.Remove(next);
                if (!_entries.TryGetValue(next, out var entry)) continue;

                if (!next.StreamingState.Pending &&
                    next.StreamingState.RenderedLength == next.Content.Length)
                    continue;

                entry.Render();
                _afterEachRender();

                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
        catch
        {
        }
        finally
        {
            _pumpActive = false;
        }

        // 必须无条件重试：Reset() 会提升 epoch，旧泵退出时若仍在等 _pumpActive（新请求被 EnsurePump 忽略），
        // 只按旧 epoch 判断会导致新会话的待渲染队列再也不会被处理。
        EnsurePump();
    }
}
