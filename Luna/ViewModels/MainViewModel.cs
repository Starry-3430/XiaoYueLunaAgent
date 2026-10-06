using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Luna.Models;
using Luna.Services;
using Luna.Services.Data;

namespace Luna.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SessionRepository _sessionRepo;
    private readonly ChatGenerationService _generation;
    private readonly FileStorageService _fileStorage;
    private readonly IDocumentConverterService _documentConverter;
    private readonly ILogger<MainViewModel> _logger;
    private string? _currentSessionId;
    private SessionRuntime? _runtime;
    private readonly ObservableCollection<ChatMessage> _emptyMessages = new();

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private bool _isBusy;

    private ObservableCollection<ChatMessage> _messages = new();
    public ObservableCollection<ChatMessage> Messages
    {
        get => _messages;
        private set => SetProperty(ref _messages, value);
    }

    /// <summary>待发送附件列表（发送前暂存，发送成功后清空）。</summary>
    public ObservableCollection<Attachment> PendingAttachments { get; } = new();

    public MainViewModel(SessionRepository sessionRepo, ChatGenerationService generation,
        FileStorageService fileStorage, IDocumentConverterService documentConverter,
        ILogger<MainViewModel> logger)
    {
        _sessionRepo = sessionRepo;
        _generation = generation;
        _fileStorage = fileStorage;
        _documentConverter = documentConverter;
        _logger = logger;
        _logger.LogInformation("MainViewModel 已创建");
    }

    /// <summary>当前会话运行期对象；界面状态全部来自它，生成在后台由服务推进。</summary>
    private SessionRuntime? Runtime
    {
        get => _runtime;
        set
        {
            if (ReferenceEquals(_runtime, value)) return;
            if (_runtime is not null) _runtime.PropertyChanged -= Runtime_PropertyChanged;
            _runtime = value;
            if (_runtime is not null) _runtime.PropertyChanged += Runtime_PropertyChanged;

            Messages = _runtime?.Messages ?? _emptyMessages;
            IsBusy = _runtime?.IsBusy ?? false;
            Status = _runtime?.Status ?? "就绪";
        }
    }

    private void Runtime_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionRuntime.IsBusy):
                IsBusy = _runtime?.IsBusy ?? false;
                break;
            case nameof(SessionRuntime.Status):
                Status = _runtime?.Status ?? "就绪";
                break;
        }
    }

    /// <summary>选择附件（多选），加入待发送列表。</summary>
    [RelayCommand]
    private void AddAttachment()
    {
        foreach (var attachment in AttachmentHelper.PickFiles())
            PendingAttachments.Add(attachment);
    }

    /// <summary>从待发送列表移除附件。</summary>
    [RelayCommand]
    private void RemoveAttachment(Attachment? attachment)
    {
        if (attachment is not null) PendingAttachments.Remove(attachment);
    }

    /// <summary>清空待发送附件。</summary>
    [RelayCommand]
    private void ClearAttachments() => PendingAttachments.Clear();

    private async Task<List<Attachment>> PrepareAttachmentsAsync()
    {
        var prepared = new List<Attachment>();
        foreach (var attachment in PendingAttachments.ToList())
        {
            var sourcePath = attachment.StoredPath;
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) continue;

            try
            {
                var stored = await _fileStorage.StoreAsync(sourcePath);
                attachment.Sha256 = stored.Sha256;
                attachment.StoredPath = stored.StoredPath;
                attachment.FileSize = stored.FileSize;
                attachment.ConvertedMarkdown = await _documentConverter.ConvertToMarkdownAsync(stored.StoredPath);
                prepared.Add(attachment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "准备附件失败：{File}", attachment.FileName);
            }
        }
        return prepared;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var hasAttachments = PendingAttachments.Count > 0;
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(InputText) && !hasAttachments) return;

        var userText = InputText.Trim();
        InputText = string.Empty;

        try
        {
            var prepared = hasAttachments ? await PrepareAttachmentsAsync() : new List<Attachment>();
            if (string.IsNullOrWhiteSpace(userText) && prepared.Count > 0)
                userText = "请阅读我发送的附件内容。";

            if (_currentSessionId is null)
            {
                var session = new Session();
                await _sessionRepo.InsertAsync(session);
                _currentSessionId = session.Id;
            }

            var sessionId = _currentSessionId;
            Runtime = await _generation.GetOrLoadAsync(sessionId);

            // 生成在服务中后台执行：即使窗口隐藏/切换，输出也会继续并写库。
            await _generation.SendAsync(sessionId, userText, prepared);

            PendingAttachments.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送失败");
            Status = "出错：" + ex.Message;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_currentSessionId is not null)
            _generation.Cancel(_currentSessionId);
    }

    public void NewChat()
    {
        _currentSessionId = null;
        Runtime = null;
        InputText = string.Empty;
        PendingAttachments.Clear();
        Status = "就绪";
    }

    [RelayCommand]
    public async Task RewriteMessageAsync(ChatMessage msg)
    {
        if (_currentSessionId is null || IsBusy) return;
        await _generation.RewriteAsync(_currentSessionId, msg);
    }
}
