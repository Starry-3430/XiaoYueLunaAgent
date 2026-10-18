using System.Collections.Concurrent;
using System.Windows;
using Luna.Models;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

/// <summary>
/// 定时提醒：轮询 Tasks 表中到点的提醒，通过 <see cref="ReminderDue"/> 通知 UI。
/// 轮询由 <see cref="Jobs.ReminderPollJob"/> 驱动；同一待办在同一 RemindAtUtc 下只触发一次
/// （稍后重设时间后会再次触发）。
/// </summary>
public class ReminderService : IDisposable
{
    private readonly TaskRepository _repo;
    private readonly ILogger<ReminderService> _logger;

    private readonly ConcurrentDictionary<string, string> _triggered = new(StringComparer.Ordinal);
    private int _polling;

    /// <summary>提醒到点（在 UI 线程触发）。</summary>
    public event Action<TaskItem>? ReminderDue;

    public ReminderService(TaskRepository repo, ILogger<ReminderService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>轮询一次到点提醒；由后台调度器调用。</summary>
    public async Task PollAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
            return;

        try
        {
            var due = await _repo.GetDueRemindersAsync(DateTime.UtcNow);
            foreach (var task in due)
            {
                ct.ThrowIfCancellationRequested();

                var stamp = task.RemindAtUtc?.ToString("O") ?? string.Empty;
                if (_triggered.TryGetValue(task.Id, out var seen) && seen == stamp)
                    continue;

                _triggered[task.Id] = stamp;
                await _repo.MarkRemindedAsync(task.Id, DateTime.UtcNow);
                _logger.LogInformation("触发提醒：{Id} {Title}", task.Id, task.Title);
                RaiseReminderDue(task);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 应用退出时的正常取消
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "轮询提醒失败");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    /// <summary>在 UI 线程触发 <see cref="ReminderDue"/>，保持与迁移前一致。</summary>
    private void RaiseReminderDue(TaskItem task)
    {
        var handler = ReminderDue;
        if (handler is null) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            handler(task);
        else
            dispatcher.BeginInvoke(() => handler(task));
    }

    public Task CompleteAsync(string id)
    {
        _triggered.TryRemove(id, out _);
        return _repo.CompleteAsync(id);
    }

    public Task SnoozeAsync(string id, int minutes)
    {
        _triggered.TryRemove(id, out _);
        return _repo.SnoozeAsync(id, DateTime.UtcNow.AddMinutes(minutes));
    }

    public void Dispose()
    {
        ReminderDue = null;
    }
}
