using System.Windows.Threading;
using Luna.Models;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

/// <summary>
/// 定时提醒：后台轮询 Tasks 表中到点的提醒，通过 <see cref="ReminderDue"/> 通知 UI。
/// 同一待办在同一 RemindAtUtc 下只触发一次（稍后重设时间后会再次触发）。
/// </summary>
public class ReminderService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly TaskRepository _repo;
    private readonly ILogger<ReminderService> _logger;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, string> _triggered = new();
    private bool _polling;

    /// <summary>提醒到点（在 UI 线程触发）。</summary>
    public event Action<TaskItem>? ReminderDue;

    public ReminderService(TaskRepository repo, ILogger<ReminderService> logger)
    {
        _repo = repo;
        _logger = logger;

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = PollAsync();
        _logger.LogInformation("提醒服务已启动（每 {Seconds} 秒轮询）", PollInterval.TotalSeconds);
    }

    public async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var due = await _repo.GetDueRemindersAsync(DateTime.UtcNow);
            foreach (var task in due)
            {
                var stamp = task.RemindAtUtc?.ToString("O") ?? string.Empty;
                if (_triggered.TryGetValue(task.Id, out var seen) && seen == stamp)
                    continue;

                _triggered[task.Id] = stamp;
                await _repo.MarkRemindedAsync(task.Id, DateTime.UtcNow);
                _logger.LogInformation("触发提醒：{Id} {Title}", task.Id, task.Title);
                ReminderDue?.Invoke(task);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "轮询提醒失败");
        }
        finally
        {
            _polling = false;
        }
    }

    public Task CompleteAsync(string id)
    {
        _triggered.Remove(id);
        return _repo.CompleteAsync(id);
    }

    public Task SnoozeAsync(string id, int minutes)
    {
        _triggered.Remove(id);
        return _repo.SnoozeAsync(id, DateTime.UtcNow.AddMinutes(minutes));
    }

    public void Dispose()
    {
        _timer.Stop();
        ReminderDue = null;
    }
}
