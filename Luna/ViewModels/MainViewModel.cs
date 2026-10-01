using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Luna.Models;
using Luna.Services;

namespace Luna.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IAiService _aiService;
    private readonly SessionRepository _sessionRepo;
    private readonly MessageRepository _messageRepo;
    private readonly ILogger<MainViewModel> _logger;
    private CancellationTokenSource? _cts;
    private string? _currentSessionId;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private bool _isBusy;

    private readonly object _chunkLock = new();
    private readonly List<StreamChunk> _chunkBuffer = new();

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public MainViewModel(IAiService aiService, SessionRepository sessionRepo,
        MessageRepository messageRepo, ILogger<MainViewModel> logger)
    {
        _aiService = aiService;
        _sessionRepo = sessionRepo;
        _messageRepo = messageRepo;
        _logger = logger;
        _logger.LogInformation("MainViewModel 已创建");
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsBusy) return;

        var userText = InputText.Trim();
        InputText = string.Empty;

        try
        {
            if (_currentSessionId is null)
            {
                var session = new Session();
                await _sessionRepo.InsertAsync(session);
                _currentSessionId = session.Id;
            }

            var turnId = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            var logicalDate = now.ToString("yyyy-MM-dd");

            var userMsg = new Message
            {
                SessionId = _currentSessionId,
                TurnId = turnId,
                Role = "user",
                Content = userText,
                CreatedAtUtc = now,
                LogicalDate = logicalDate,
            };
            await _messageRepo.InsertAsync(userMsg);

            Messages.Add(new ChatMessage { Role = "user", Content = userText });

            var uiReply = new ChatMessage { Role = "assistant", Content = string.Empty, IsStreaming = true };
            Messages.Add(uiReply);

            IsBusy = true;
            Status = "思考中…";
            _cts = new CancellationTokenSource();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            var buffer = new StringBuilder();
            var reasoningBuffer = new StringBuilder();

            timer.Tick += (_, _) =>
            {
                StreamChunk[] batch;
                lock (_chunkLock)
                {
                    if (_chunkBuffer.Count == 0) return;
                    batch = _chunkBuffer.ToArray();
                    _chunkBuffer.Clear();
                }
                foreach (var c in batch)
                {
                    if (c.Content is not null)
                        buffer.Append(c.Content);
                    if (c.ReasoningContent is not null)
                        reasoningBuffer.Append(c.ReasoningContent);
                }
                uiReply.Content = buffer.ToString();
                uiReply.ReasoningContent = reasoningBuffer.ToString();
            };

            try
            {
                var consumeTask = Task.Run(() => ConsumeStreamAsync(_cts.Token), _cts.Token);

                timer.Start();
                await consumeTask;

                StreamChunk[] remaining;
                lock (_chunkLock)
                {
                    remaining = _chunkBuffer.ToArray();
                    _chunkBuffer.Clear();
                }
                foreach (var c in remaining)
                {
                    if (c.Content is not null)
                        buffer.Append(c.Content);
                    if (c.ReasoningContent is not null)
                        reasoningBuffer.Append(c.ReasoningContent);
                }
                if (remaining.Length > 0)
                {
                    uiReply.Content = buffer.ToString();
                    uiReply.ReasoningContent = reasoningBuffer.ToString();
                }

                var replyContent = uiReply.Content;
                var reasoningContent = uiReply.ReasoningContent;

                var assistantMsg = new Message
                {
                    SessionId = _currentSessionId,
                    TurnId = turnId,
                    Role = "assistant",
                    Content = replyContent,
                    ReasoningContent = reasoningContent,
                    CreatedAtUtc = DateTime.UtcNow,
                    LogicalDate = logicalDate,
                };
                await _messageRepo.InsertAsync(assistantMsg);

                await _sessionRepo.TouchAsync(_currentSessionId);

                WeakReferenceMessenger.Default.Send(new SessionUpdateMessage());
                Status = "就绪";
            }
            catch (OperationCanceledException)
            {
                Status = "就绪（已取消）";
                _logger.LogInformation("请求被取消");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI 回复失败");
                Status = "出错：" + ex.Message;
            }
            finally
            {
                uiReply.IsStreaming = false;
                timer.Stop();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送失败");
            Status = "出错：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task ConsumeStreamAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _aiService.ChatStreamAsync(Messages, ct))
            {
                lock (_chunkLock)
                    _chunkBuffer.Add(chunk);
            }
        }
        catch (OperationCanceledException)
        {
            // normal cancellation
        }
    }

    public void NewChat()
    {
        Messages.Clear();
        InputText = string.Empty;
        Status = "就绪";
        _currentSessionId = null;
    }

    public async Task RewriteMessageAsync(ChatMessage msg)
    {
        if (IsBusy) return;

        var index = Messages.IndexOf(msg);
        if (index < 0) return;

        while (Messages.Count > index)
            Messages.RemoveAt(index);

        string userText = "";
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].Role == "user")
            {
                userText = Messages[i].Content;
                break;
            }
        }

        if (string.IsNullOrEmpty(userText)) return;

        IsBusy = true;
        Status = "思考中…";
        _cts = new CancellationTokenSource();

        try
        {
            var uiReply = new ChatMessage { Role = "assistant", Content = "" };
            Messages.Add(uiReply);

            var replyContent = await StreamIntoAsync(uiReply, _cts.Token);

            Status = "就绪";
        }
        catch (OperationCanceledException)
        {
            Status = "就绪（已取消）";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重写失败");
            Status = "出错：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task<string> StreamIntoAsync(ChatMessage uiReply, CancellationToken ct)
    {
        var lockObj = new object();
        var buffer = new List<StreamChunk>();
        var sb = new StringBuilder();
        var reasoningSb = new StringBuilder();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

        timer.Tick += (_, _) =>
        {
            StreamChunk[] batch;
            lock (lockObj)
            {
                if (buffer.Count == 0) return;
                batch = buffer.ToArray();
                buffer.Clear();
            }
            foreach (var c in batch)
            {
                if (c.Content is not null)
                    sb.Append(c.Content);
                if (c.ReasoningContent is not null)
                    reasoningSb.Append(c.ReasoningContent);
            }
            uiReply.Content = sb.ToString();
            uiReply.ReasoningContent = reasoningSb.ToString();
        };

        try
        {
            uiReply.IsStreaming = true;

            var consumeTask = Task.Run(async () =>
            {
                await foreach (var chunk in _aiService.ChatStreamAsync(Messages, ct))
                {
                    lock (lockObj)
                        buffer.Add(chunk);
                }
            }, ct);

            timer.Start();
            await consumeTask;

            StreamChunk[] remaining;
            lock (lockObj)
            {
                remaining = buffer.ToArray();
                buffer.Clear();
            }
            foreach (var c in remaining)
            {
                if (c.Content is not null)
                    sb.Append(c.Content);
                if (c.ReasoningContent is not null)
                    reasoningSb.Append(c.ReasoningContent);
            }
            if (remaining.Length > 0)
            {
                uiReply.Content = sb.ToString();
                uiReply.ReasoningContent = reasoningSb.ToString();
            }

            return uiReply.Content;
        }
        finally
        {
            uiReply.IsStreaming = false;
            timer.Stop();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }
}