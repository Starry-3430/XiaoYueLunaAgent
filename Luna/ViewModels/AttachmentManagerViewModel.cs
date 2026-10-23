using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Luna.Localization;
using Luna.Models;
using Luna.Services;
using Luna.Services.Data;

namespace Luna.ViewModels;

/// <summary>附件管理页：查看已存储附件并删除以释放磁盘空间。</summary>
public partial class AttachmentManagerViewModel : ObservableObject
{
    private readonly AttachmentRepository _repository;
    private readonly FileStorageService _storage;
    private readonly ILogger<AttachmentManagerViewModel> _logger;

    public ObservableCollection<AttachmentItem> Items { get; } = new();

    private bool _loaded;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>附件存储数量上限的明文说明（设置页展示）。</summary>
    public string RetentionText =>
        LocalizationService.Instance.Format("Attachment_Retention_Desc", AttachmentHelper.MaxStoredCount);

    [ObservableProperty]
    private bool _isBusy;

    public AttachmentManagerViewModel(AttachmentRepository repository, FileStorageService storage,
        ILogger<AttachmentManagerViewModel> logger)
    {
        _repository = repository;
        _storage = storage;
        _logger = logger;

        Summary = LocalizationService.Instance["Attachment_Summary_NotLoaded"];
        LocalizationService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "Item[]" or nameof(LocalizationService.Culture))
            {
                RefreshSummaryText();
                OnPropertyChanged(nameof(RetentionText));
            }
        };
    }

    private void RefreshSummaryText()
    {
        if (!_loaded)
        {
            Summary = LocalizationService.Instance["Attachment_Summary_NotLoaded"];
            return;
        }

        UpdateSummary();
    }

    /// <summary>重新加载附件列表与总大小。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var attachments = await _repository.GetAllAsync();
            Items.Clear();
            long total = 0;
            foreach (var a in attachments)
            {
                Items.Add(new AttachmentItem(a));
                total += a.FileSize;
            }
            _loaded = true;
            Summary = Items.Count == 0
                ? LocalizationService.Instance["Attachment_Summary_Empty"]
                : LocalizationService.Instance.Format("Attachment_Summary_Count", Items.Count, AttachmentHelper.FormatSize(total));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载附件列表失败");
            Summary = LocalizationService.Instance.Format("Attachment_Load_Failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>删除单个附件（数据库记录 + blob 文件）。</summary>
    [RelayCommand]
    private async Task DeleteAsync(AttachmentItem? item)
    {
        if (item is null) return;

        try
        {
            await _repository.DeleteAsync(item.Attachment.Id);

            // 内容寻址下同一 blob 可能被多条记录引用，只有再无引用时才删除文件。
            if (!string.IsNullOrEmpty(item.Attachment.Sha256) &&
                await _repository.GetBySha256Async(item.Attachment.Sha256) is null)
            {
                _storage.DeleteBlob(item.Attachment.Sha256);
            }

            Items.Remove(item);
            UpdateSummary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除附件失败");
            Summary = LocalizationService.Instance.Format("Attachment_Delete_Failed", ex.Message);
        }
    }

    /// <summary>打开附件存储目录。</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_storage.BlobsRoot);
            Process.Start(new ProcessStartInfo(_storage.BlobsRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开附件目录失败");
            Summary = LocalizationService.Instance.Format("Attachment_Open_Failed", ex.Message);
        }
    }

    private void UpdateSummary()
    {
        if (Items.Count == 0)
        {
            Summary = LocalizationService.Instance["Attachment_Summary_Empty"];
            return;
        }

        long total = Items.Sum(i => i.FileSize);
        Summary = LocalizationService.Instance.Format("Attachment_Summary_Count", Items.Count, AttachmentHelper.FormatSize(total));
    }
}

/// <summary>附件管理列表项（展示用）。</summary>
public sealed class AttachmentItem
{
    public AttachmentItem(Attachment attachment) => Attachment = attachment;

    public Attachment Attachment { get; }

    public string FileName => string.IsNullOrEmpty(Attachment.FileName) ? LocalizationService.Instance["Attachment_Unnamed"] : Attachment.FileName;

    public long FileSize => Attachment.FileSize;

    public string SizeText => AttachmentHelper.FormatSize(FileSize);

    public string CreatedText => Attachment.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string Location => string.IsNullOrEmpty(Attachment.StoredPath) ? "—" : Attachment.StoredPath;
}
