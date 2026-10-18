using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

/// <summary>在应用运行期间采集文本剪贴板变化，供剪贴板历史工具查询。</summary>
public sealed class ClipboardHistoryService : IDisposable
{
    public sealed record Entry(int Id, DateTimeOffset CapturedAt, string Text);

    private const int MaxEntries = 50;
    private readonly object _gate = new();
    private readonly ILogger<ClipboardHistoryService> _logger;
    private readonly ToolSettingsService _toolSettings;
    private readonly List<Entry> _entries = [];
    private DispatcherTimer? _timer;
    private string? _lastText;
    private int _nextId;

    public ClipboardHistoryService(ILogger<ClipboardHistoryService> logger, ToolSettingsService toolSettings)
    {
        _logger = logger;
        _toolSettings = toolSettings;
    }

    public void Start()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => CaptureCurrent(), Application.Current.Dispatcher);
        _timer.Start();
        CaptureCurrent();
    }

    public IReadOnlyList<Entry> GetEntries(int limit)
    {
        lock (_gate) return _entries.Take(Math.Clamp(limit, 1, MaxEntries)).ToArray();
    }

    public bool TryGet(int id, out Entry? entry)
    {
        lock (_gate)
        {
            entry = _entries.FirstOrDefault(x => x.Id == id);
            return entry is not null;
        }
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    public async Task<bool> CopyAsync(int id)
    {
        if (!TryGet(id, out var entry) || entry is null) return false;
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() => Clipboard.SetText(entry.Text));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "复制剪贴板历史失败：{Id}", id);
            return false;
        }
    }

    private void CaptureCurrent()
    {
        if (!_toolSettings.IsEnabled("clipboard_history")) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, _lastText, StringComparison.Ordinal)) return;
            _lastText = text;
            lock (_gate)
            {
                _entries.Insert(0, new Entry(++_nextId, DateTimeOffset.Now, text));
                if (_entries.Count > MaxEntries) _entries.RemoveAt(_entries.Count - 1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "采集剪贴板失败");
        }
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }
}
