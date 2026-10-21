using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using System.NativeTray;

namespace Luna.Services;

/// <summary>
/// 系统通知：优先使用 Windows Toast（彩色应用图标），失败时回退到托盘气泡。
/// 供 send_notification 工具与后续“定时提醒”复用。
/// </summary>
public class NotificationService
{
    private const string ActionArgument = "luna_action";
    private const string ActionOpenSession = "open_session";
    private const string SessionIdArgument = "luna_session_id";

    private readonly TrayIconHost? _tray;
    private readonly ILogger<NotificationService> _logger;
    private bool _toastUnavailable;
    private string? _balloonSessionId;

    public NotificationService(TrayIconHost? tray, ILogger<NotificationService> logger)
    {
        _tray = tray;
        _logger = logger;

        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "订阅 Toast 激活事件失败");
        }

        if (_tray is not null)
        {
            _tray.BalloonTipClicked += (_, _) =>
            {
                var sessionId = _balloonSessionId;
                _balloonSessionId = null;
                if (!string.IsNullOrWhiteSpace(sessionId))
                    RaiseSessionActivated(sessionId);
            };
            _tray.BalloonTipClosed += (_, _) => _balloonSessionId = null;
        }
    }

    /// <summary>用户点击带会话目标的通知时触发，参数为要打开的会话 ID。</summary>
    public event Action<string>? SessionActivated;

    /// <summary>显示一条系统通知；提供 <paramref name="sessionId"/> 时点击通知会打开对应会话。</summary>
    public void Notify(string title, string message, int timeoutMs = 5000, string? sessionId = null)
    {
        void Show()
        {
            if (!_toastUnavailable)
            {
                try
                {
                    var builder = new ToastContentBuilder()
                        .AddText(title)
                        .AddText(message);

                    if (!string.IsNullOrWhiteSpace(sessionId))
                    {
                        builder
                            .AddArgument(ActionArgument, ActionOpenSession)
                            .AddArgument(SessionIdArgument, sessionId);
                    }

                    builder.Show();

                    _logger.LogInformation("已发送系统 Toast 通知：{Title}", title);
                    return;
                }
                catch (Exception ex)
                {
                    _toastUnavailable = true;
                    _logger.LogWarning(ex, "发送 Toast 通知失败，回退到托盘气泡");
                }
            }

            ShowBalloonFallback(title, message, timeoutMs, sessionId);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Show();
        else
            dispatcher.Invoke(Show);
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(e.Argument))
                return;

            var args = ToastArguments.Parse(e.Argument);
            if (!args.TryGetValue(ActionArgument, out var action) ||
                !string.Equals(action, ActionOpenSession, StringComparison.Ordinal) ||
                !args.TryGetValue(SessionIdArgument, out var sessionId) ||
                string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            RaiseSessionActivated(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理 Toast 激活失败");
        }
    }

    private void RaiseSessionActivated(string sessionId)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        if (dispatcher.CheckAccess())
            SessionActivated?.Invoke(sessionId);
        else
            dispatcher.BeginInvoke(() => SessionActivated?.Invoke(sessionId));
    }

    private void ShowBalloonFallback(string title, string message, int timeoutMs, string? sessionId)
    {
        if (_tray is null)
        {
            _logger.LogWarning("托盘图标不可用，通知被丢弃：{Title} - {Message}", title, message);
            return;
        }

        try
        {
            _balloonSessionId = sessionId;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = message;
            _tray.BalloonTipIcon = TrayToolTipIcon.None;
            _tray.ShowBalloonTip(timeoutMs);
            _logger.LogInformation("已发送托盘通知：{Title}", title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送托盘通知失败");
        }
    }
}
