using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Luna.Models;
using Luna.Services;
using Microsoft.Extensions.Logging;

namespace Luna.ViewModels;

/// <summary>日记本页面：加载所有逻辑日的日记，供横向分页浏览、检索与重试。</summary>
public partial class DiaryViewModel : ObservableObject
{
    private readonly DiaryService _diary;
    private readonly AiSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly ILogger<DiaryViewModel> _logger;

    public ObservableCollection<DiaryDayView> Days { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>日记本是否启用（是否自动整理日记）。</summary>
    [ObservableProperty]
    private bool _diaryEnabled;

    public string Nickname { get; }

    public DiaryViewModel(DiaryService diary, AiSettings settings, SettingsService settingsService,
        ILogger<DiaryViewModel> logger)
    {
        _diary = diary;
        _settings = settings;
        _settingsService = settingsService;
        _logger = logger;
        Nickname = string.IsNullOrWhiteSpace(settings.Nickname) ? "Luna" : settings.Nickname;
        _diaryEnabled = settings.DiaryEnabled;
    }

    /// <summary>设置日记本开关并持久化；重新启用时立即补写最近窗口内的日记。</summary>
    public void SetDiaryEnabled(bool enabled)
    {
        DiaryEnabled = enabled;
        if (_settings.DiaryEnabled == enabled) return;

        _settings.DiaryEnabled = enabled;
        _settingsService.Save(_settings);

        if (enabled) _diary.Trigger();
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var days = await _diary.GetDaysAsync();
            Days.Clear();
            foreach (var d in days)
                Days.Add(d);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载日记失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RetryAsync(DiaryDayView day)
    {
        try
        {
            await _diary.RetryAsync(day.LogicalDate);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重试日记失败：{Date}", day.LogicalDate);
        }
    }

    /// <summary>检索并返回第一个命中日在 Days 中的索引；无命中返回 null。</summary>
    public async Task<int?> SearchAsync()
    {
        var q = SearchText.Trim();
        if (q.Length == 0) return null;

        var hits = await _diary.SearchAsync(q);
        if (hits.Count == 0) return null;

        var first = hits[0];
        for (var i = 0; i < Days.Count; i++)
        {
            if (Days[i].LogicalDate == first)
                return i;
        }
        return null;
    }
}
