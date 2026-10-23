using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Luna.Controls;
using Luna.Localization;
using Luna.Models;
using Luna.Services.Data;
using Luna.Services.Tools;
using Microsoft.Extensions.Logging;
using WpfMarkdownViewer.Controls;
using WpfMarkdownViewer.Rendering;

namespace Luna.Services;

/// <summary>用户在授权弹窗中的选择。</summary>
public enum ToolPermissionDecision
{
    AllowOnce,
    AllowAlways,
    Deny,
}

/// <summary>
/// 工具权限机制：按“对话（会话）”记录“始终允许”的工具，并把允许记住的只读工具
/// 持久化到数据库；执行中/高风险工具前弹出授权确认框。
/// </summary>
public class ToolPermissionService
{
    private readonly ToolPermissionRepository _repo;
    private readonly ILogger<ToolPermissionService> _logger;
    private readonly Task _loadTask;

    private readonly Dictionary<string, HashSet<string>> _alwaysAllowed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _persistedAllowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _conversationGate = new();

    /// <summary>允许“始终允许（记住）”并持久化的只读工具白名单。</summary>
    private static readonly HashSet<string> PersistableTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "read_screen", "search_files", "file_open",
        "web_search", "fetch_url", "read_clipboard", "list_tasks", "list_facts",
        "diary_search", "turn_search", "file_tree",
    };

    public ToolPermissionService(ToolPermissionRepository repo, ILogger<ToolPermissionService> logger)
    {
        _repo = repo;
        _logger = logger;
        _loadTask = LoadPersistedPermissionsAsync();
    }

    private async Task LoadPersistedPermissionsAsync()
    {
        try
        {
            var tools = await _repo.GetPersistedPermissionsAsync();
            foreach (var tool in tools)
                _persistedAllowed[tool] = 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载持久化工具权限失败");
        }
    }

    /// <summary>该工具是否允许持久化“始终允许”。</summary>
    public static bool IsPersistable(ITool tool)
        => !IsAlwaysConfirm(tool) && PersistableTools.Contains(tool.Name);

    /// <summary>该风险等级（或强制确认标记）是否需要弹窗确认。</summary>
    public static bool NeedsConfirmation(ITool tool)
    {
        // PowerShell 每次调用都必须确认，且不允许"始终允许"。
        if (IsAlwaysConfirm(tool)) return true;
        return tool.RequiresUserConfirmation || tool.Risk is ToolRiskLevel.Medium or ToolRiskLevel.High;
    }

    /// <summary>永不缓存授权、每次都需确认的工具（如 powershell）。</summary>
    public static bool IsAlwaysConfirm(ITool tool)
        => string.Equals(tool.Name, "powershell", StringComparison.OrdinalIgnoreCase);

    /// <summary>当前对话/全局持久化权限是否已允许该工具。</summary>
    public async Task<bool> IsAlwaysAllowedAsync(string? conversationId, string toolName)
    {
        await _loadTask.ConfigureAwait(false);
        if (_persistedAllowed.ContainsKey(toolName)) return true;

        lock (_conversationGate)
        {
            return _alwaysAllowed.TryGetValue(ConversationKey(conversationId), out var set)
                   && set.Contains(toolName);
        }
    }

    /// <summary>同步版本：只检查当前已加载的内存状态。</summary>
    public bool IsAlwaysAllowed(string? conversationId, string toolName)
    {
        if (_persistedAllowed.ContainsKey(toolName)) return true;

        lock (_conversationGate)
        {
            return _alwaysAllowed.TryGetValue(ConversationKey(conversationId), out var set)
                   && set.Contains(toolName);
        }
    }

    /// <summary>在本轮对话内始终允许某工具。</summary>
    public void AllowForConversation(string? conversationId, string toolName)
    {
        lock (_conversationGate)
        {
            var key = ConversationKey(conversationId);
            if (!_alwaysAllowed.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _alwaysAllowed[key] = set;
            }
            set.Add(toolName);
        }
    }

    /// <summary>清除某对话的“始终允许”记录（如新建/删除会话时）。</summary>
    public void ClearConversation(string? conversationId)
    {
        lock (_conversationGate)
            _alwaysAllowed.Remove(ConversationKey(conversationId));
    }

    /// <summary>
    /// 应用授权结果：允许记住的工具写入数据库，其余仅在本对话生效。
    /// </summary>
    public async Task ApplyDecisionAsync(string? conversationId, IEnumerable<ITool> tools,
        ToolPermissionDecision decision)
    {
        if (decision != ToolPermissionDecision.AllowAlways)
            return;

        foreach (var tool in tools)
        {
            if (IsAlwaysConfirm(tool)) continue;

            if (IsPersistable(tool))
            {
                _persistedAllowed[tool.Name] = 0;
                try
                {
                    await _repo.AddPersistentPermissionAsync(tool.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "持久化工具权限失败：{Tool}", tool.Name);
                }
            }
            else
            {
                AllowForConversation(conversationId, tool.Name);
            }
        }
    }

    /// <summary>撤销单个工具的持久化权限。</summary>
    public async Task RemovePersistentPermissionAsync(string toolName)
    {
        _persistedAllowed.TryRemove(toolName, out _);
        await _repo.RemovePersistentPermissionAsync(toolName);
    }

    /// <summary>清除全部持久化权限。</summary>
    public async Task ClearPersistentPermissionsAsync()
    {
        _persistedAllowed.Clear();
        await _repo.ClearAllPersistentPermissionsAsync();
    }

    /// <summary>弹出授权确认框并返回用户选择。</summary>
    public ToolPermissionDecision RequestConfirmation(ITool tool, string argumentsJson)
    {
        var decision = ToolPermissionDecision.Deny;

        void ShowDialog()
        {
            var dialog = new LunaDialog
            {
                Owner = GetOwner(),
                DialogTitle = LocalizationService.Instance["Dialog_ToolPermission_Title"],
                DialogContent = BuildContent(tool, argumentsJson),
            };

            var buttons = new ObservableCollection<DialogButton>
            {
                new()
                {
                    Text = LocalizationService.Instance["Dialog_ToolPermission_AllowOnce"],
                    StyleKey = "StyleBeige",
                    Command = new RelayCommand(() =>
                    {
                        decision = ToolPermissionDecision.AllowOnce;
                        dialog.Close();
                    }),
                },
            };

            // 只有允许记住的只读工具才提供“始终允许（记住）”。
            if (IsPersistable(tool))
            {
                buttons.Add(new()
                {
                    Text = LocalizationService.Instance["Dialog_ToolPermission_AllowAlways"],
                    StyleKey = "StyleDanger",
                    Command = new RelayCommand(() =>
                    {
                        decision = ToolPermissionDecision.AllowAlways;
                        dialog.Close();
                    }),
                });
            }

            buttons.Add(new()
            {
                Text = LocalizationService.Instance["Dialog_ToolPermission_Deny"],
                StyleKey = "StyleBeige",
                Command = new RelayCommand(() =>
                {
                    decision = ToolPermissionDecision.Deny;
                    dialog.Close();
                }),
            });

            dialog.Buttons = buttons;

            dialog.ShowDialog();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ShowDialog();
        else
            dispatcher.Invoke(ShowDialog);

        return decision;
    }

    /// <summary>
    /// 弹出批量授权确认框：一次展示本轮所有待确认工具，并统一返回选择。
    /// 只有 1 项时复用单工具确认框；0 项视为无需确认。
    /// </summary>
    public ToolPermissionDecision RequestBatchConfirmation(
        string? conversationId, IReadOnlyList<(ITool Tool, ToolCallEntry Entry)> tools)
    {
        if (tools.Count == 0) return ToolPermissionDecision.AllowOnce;
        if (tools.Count == 1)
            return RequestConfirmation(tools[0].Tool, tools[0].Entry.ArgumentsJson);

        var decision = ToolPermissionDecision.Deny;

        void ShowDialog()
        {
            var dialog = new LunaDialog
            {
                Owner = GetOwner(),
                DialogTitle = LocalizationService.Instance.Format(
                    "Dialog_ToolPermission_BatchTitle", tools.Count),
                DialogContent = BuildBatchContent(tools),
            };

            var buttons = new ObservableCollection<DialogButton>
            {
                new()
                {
                    Text = LocalizationService.Instance["Dialog_ToolPermission_AllowOnce"],
                    StyleKey = "StyleBeige",
                    Command = new RelayCommand(() =>
                    {
                        decision = ToolPermissionDecision.AllowOnce;
                        dialog.Close();
                    }),
                },
            };

            // 只有整批都允许记住时，才提供“始终允许（记住）”。
            if (tools.Count > 0 && tools.All(t => IsPersistable(t.Tool)))
            {
                buttons.Add(new()
                {
                    Text = LocalizationService.Instance["Dialog_ToolPermission_AllowAlways"],
                    StyleKey = "StyleDanger",
                    Command = new RelayCommand(() =>
                    {
                        decision = ToolPermissionDecision.AllowAlways;
                        dialog.Close();
                    }),
                });
            }

            buttons.Add(new()
            {
                Text = LocalizationService.Instance["Dialog_ToolPermission_Deny"],
                StyleKey = "StyleBeige",
                Command = new RelayCommand(() =>
                {
                    decision = ToolPermissionDecision.Deny;
                    dialog.Close();
                }),
            });

            dialog.Buttons = buttons;

            dialog.ShowDialog();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ShowDialog();
        else
            dispatcher.Invoke(ShowDialog);

        return decision;
    }

    /// <summary>批量确认框内容：逐项列出工具名与格式化后的参数（可滚动）。</summary>
    private static UIElement BuildBatchContent(IReadOnlyList<(ITool Tool, ToolCallEntry Entry)> tools)
    {
        var loc = LocalizationService.Instance;
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = loc.Format("Dialog_ToolPermission_BatchBody", tools.Count),
            Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var items = new StackPanel();
        for (var i = 0; i < tools.Count; i++)
        {
            var (tool, entry) = tools[i];
            var item = new StackPanel();

            item.Children.Add(new TextBlock
            {
                Text = $"{i + 1}. {tool.DisplayName}",
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
            });
            item.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(entry.ArgumentsJson)
                    ? loc["Dialog_ToolPermission_NoArgs"]
                    : ToolArgumentFormatter.FormatArguments(tool.Name, entry.ArgumentsJson),
                Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x7B, 0x6B)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });

            items.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE0, 0xCA)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = item,
            });
        }

        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 320,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = items,
        });

        return panel;
    }

    private static UIElement BuildContent(ITool tool, string argumentsJson)
    {
        // PowerShell / code_execute 特例：直接用 Markdown 代码块展示代码，不再显示工具名与 JSON。
        if (ToolArgumentFormatter.IsPowerShell(tool.Name) || ToolArgumentFormatter.IsCodeExecute(tool.Name))
            return BuildCodeContent(tool, argumentsJson);

        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = LocalizationService.Instance["Dialog_ToolPermission_Body"],
            Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var details = new StackPanel();
        details.Children.Add(new TextBlock
        {
            Text = tool.DisplayName,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        });
        details.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(argumentsJson)
                ? LocalizationService.Instance["Dialog_ToolPermission_NoArgs"]
                : ToolArgumentFormatter.FormatArguments(tool.Name, argumentsJson),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x7B, 0x6B)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE0, 0xCA)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = details,
        });

        return panel;
    }

    /// <summary>代码类工具的确认框：正文提示 + 透明背景的 Markdown 代码块（不显示工具名）。</summary>
    private static UIElement BuildCodeContent(ITool tool, string argumentsJson)
    {
        var code = ToolArgumentFormatter.TryGetCode(tool.Name, argumentsJson)
                   ?? argumentsJson?.Trim()
                   ?? string.Empty;
        var language = ToolArgumentFormatter.IsPowerShell(tool.Name) ? "powershell" : "python";

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = LocalizationService.Instance["Dialog_ToolPermission_Body"],
            Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var markdown = $"```{language}\n{code}\n```";
        var viewer = new MarkdownDocumentView
        {
            Background = Brushes.Transparent,
            SelectionEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 560,
        };

        var style = MarkdownStyle.Light with
        {
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
            SubtleForeground = new SolidColorBrush(Color.FromRgb(0x8B, 0x7B, 0x6B)),
            InlineCodeBackground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD0, 0xBA)),
            CodeBlockBackground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE0, 0xCA)),
            Border = new SolidColorBrush(Color.FromRgb(0xD4, 0xD0, 0xBA)),
            QuoteBar = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
            ContentPadding = new Thickness(0),
            EmSize = 13,
            MonoTypeface = ThemeService.CodeTypeface,
        };
        if (ThemeService.BaseTypeface is { } baseTypeface)
            style = style with { BaseTypeface = baseTypeface };

        viewer.ApplyTheme(style);
        viewer.SetMarkdown(markdown);
        panel.Children.Add(viewer);

        return panel;
    }

    private static Window? GetOwner()
    {
        // App 会确保临时显示一个可用的宿主窗口；绝不能用从未 Show 过的窗口当 Owner。
        if (Application.Current is App app)
            return app.EnsureDialogOwner();

        return Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive && w.IsVisible);
    }

    private static string ConversationKey(string? conversationId)
        => string.IsNullOrEmpty(conversationId) ? string.Empty : conversationId;
}
