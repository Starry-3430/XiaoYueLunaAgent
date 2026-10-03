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
    private readonly ILogger<DiaryViewModel> _logger;

    public ObservableCollection<DiaryDayView> Days { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    public string Nickname { get; }

    public DiaryViewModel(DiaryService diary, AiSettings settings, ILogger<DiaryViewModel> logger)
    {
        _diary = diary;
        _logger = logger;
        Nickname = string.IsNullOrWhiteSpace(settings.Nickname) ? "Luna" : settings.Nickname;
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
