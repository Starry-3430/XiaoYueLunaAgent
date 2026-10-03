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
    private readonly TrayIconHost? _tray;
    private readonly ILogger<NotificationService> _logger;
    private bool _toastUnavailable;

    public NotificationService(TrayIconHost? tray, ILogger<NotificationService> logger)
    {
        _tray = tray;
        _logger = logger;
    }

    /// <summary>显示一条系统通知。</summary>
    public void Notify(string title, string message, int timeoutMs = 5000)
    {
        void Show()
        {
            if (!_toastUnavailable)
            {
                try
                {
                    new ToastContentBuilder()
                        .AddText(title)
                        .AddText(message)
                        .Show();

                    _logger.LogInformation("已发送系统 Toast 通知：{Title}", title);
                    return;
                }
                catch (Exception ex)
                {
                    _toastUnavailable = true;
                    _logger.LogWarning(ex, "发送 Toast 通知失败，回退到托盘气泡");
                }
            }

            ShowBalloonFallback(title, message, timeoutMs);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Show();
        else
            dispatcher.Invoke(Show);
    }

    private void ShowBalloonFallback(string title, string message, int timeoutMs)
    {
        if (_tray is null)
        {
            _logger.LogWarning("托盘图标不可用，通知被丢弃：{Title} - {Message}", title, message);
            return;
        }

        try
        {
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
