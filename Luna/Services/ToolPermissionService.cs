using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Luna.Controls;
using Luna.Localization;
using Luna.Models;
using Luna.Services.Tools;
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
/// 工具权限机制：按“对话（会话）”记录“始终允许”的工具，
/// 并在执行中/高风险工具前弹出授权确认框。拒绝时返回 <see cref="ToolPermissionDecision.Deny"/>。
/// </summary>
public class ToolPermissionService
{
    private readonly Dictionary<string, HashSet<string>> _alwaysAllowed = new(StringComparer.Ordinal);

    /// <summary>该风险等级（或强制确认标记）是否需要弹窗确认。</summary>
    public static bool NeedsConfirmation(ITool tool)
    {
        // PowerShell 每次调用都必须确认，且不允许"始终允许"。
        if (IsAlwaysConfirm(tool)) return true;
        return tool.RequiresUserConfirmation || tool.Risk is ToolRiskLevel.Medium or ToolRiskLevel.High;
    }

    /// <summary>永不缓存授权、每次都需确认的工具（如 powershell）。</summary>
    private static bool IsAlwaysConfirm(ITool tool)
        => string.Equals(tool.Name, "powershell", StringComparison.OrdinalIgnoreCase);

    /// <summary>当前对话是否已对本工具选择过“始终允许”。</summary>
    public bool IsAlwaysAllowed(string? conversationId, string toolName)
        => _alwaysAllowed.TryGetValue(ConversationKey(conversationId), out var set) && set.Contains(toolName);

    /// <summary>在本轮对话内始终允许某工具。</summary>
    public void AllowForConversation(string? conversationId, string toolName)
    {
        var key = ConversationKey(conversationId);
        if (!_alwaysAllowed.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _alwaysAllowed[key] = set;
        }
        set.Add(toolName);
    }

    /// <summary>清除某对话的“始终允许”记录（如新建/删除会话时）。</summary>
    public void ClearConversation(string? conversationId)
        => _alwaysAllowed.Remove(ConversationKey(conversationId));

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

            // powershell 不允许"始终允许"，因此不提供该选项。
            if (!IsAlwaysConfirm(tool))
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
                StyleKey = "StyleCancel",
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
        => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
           ?? Application.Current?.MainWindow;

    private static string ConversationKey(string? conversationId)
        => string.IsNullOrEmpty(conversationId) ? string.Empty : conversationId;
}
