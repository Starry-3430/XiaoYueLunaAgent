using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Luna.Controls;
using Luna.Localization;
using Luna.Models;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>
/// 向用户发起提问：缺少关键信息或需要用户做决定时，弹出输入框/选项框并阻塞等待回答。
/// 属于交互型工具，不是只读工具，因此天然作为并发屏障串行执行。
/// </summary>
public class AskUserTool : ITool
{
    private const string SchemaTemplate =
        """{"type":"object","properties":{"question":{"type":"string","description":"$question$"},"options":{"type":"array","items":{"type":"string"},"description":"$options$"}},"required":["question"]}""";

    public string Name => "ask_user";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate
        .Replace("$question$", Param("Tool_AskUser_Param_Question"))
        .Replace("$options$", Param("Tool_AskUser_Param_Options")));
    public ToolRiskLevel Risk => ToolRiskLevel.Low;

    /// <summary>用户无响应的时限；超时后自动关闭弹窗（有选项时选第一个）。</summary>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(5);

    /// <summary>比内部超时稍长，作为外层硬兜底，确保内部超时能先返回结果。</summary>
    public TimeSpan ExecutionTimeout => ResponseTimeout + TimeSpan.FromSeconds(15);

    /// <summary>对话框内的输入状态，便于确认按钮与外部点击关闭共享同一份数据。</summary>
    private sealed class InputState
    {
        public TextBox? TextBox { get; set; }
        public Popup? OptionsPopup { get; set; }
        public Func<string?> GetAnswer { get; set; } = static () => null;
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var question = GetString(args, "question");
        if (string.IsNullOrWhiteSpace(question))
            return Task.FromResult(ToolResult.Error(Text("Tool_Msg_MissingParam", "question")));

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return Task.FromResult(ToolResult.Error(Text("Tool_AskUser_Msg_Unavailable")));

        var options = GetOptions(args);
        var defaultOption = options.Count > 0 ? options[0] : null;
        var tcs = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        LunaDialog? dialog = null;

        // 内部响应超时：到点后关闭弹窗；有选项时自动采用第一个选项，填空则返回超时错误。
        using var timeoutCts = new CancellationTokenSource(ResponseTimeout);
        using var timeoutRegistration = timeoutCts.Token.Register(() =>
        {
            if (tcs.Task.IsCompleted)
                return;

            var result = defaultOption is not null
                ? ToolResult.Ok(Text("Tool_AskUser_Msg_TimeoutDefault", defaultOption))
                : ToolResult.Timeout(Text("Tool_AskUser_Msg_Timeout"));
            tcs.TrySetResult(result);

            var current = dialog;
            if (current is not null)
            {
                current.Dispatcher.BeginInvoke(() =>
                {
                    if (current.IsVisible)
                        current.Close();
                });
            }
        });

        using var registration = ct.Register(() =>
        {
            tcs.TrySetResult(ToolResult.Canceled(Text("Tool_AskUser_Msg_Cancelled")));

            // 取消时关闭仍然打开的弹窗；BeginInvoke 避免从计时器线程直接操作 UI。
            var current = dialog;
            if (current is not null)
            {
                current.Dispatcher.BeginInvoke(() =>
                {
                    if (current.IsVisible)
                        current.Close();
                });
            }
        });

        void ShowDialog()
        {
            if (ct.IsCancellationRequested || tcs.Task.IsCompleted)
                return;

            var state = new InputState();
            var content = BuildContent(question, options, state);
            var dlg = new LunaDialog
            {
                Owner = GetOwner(),
                DialogTitle = LocalizationService.Instance["Dialog_AskUser_Title"],
                DialogContent = content,
            };

            // 输入框使用与设置页一致的圆角样式。
            if (state.TextBox is not null && dlg.TryFindResource("DialogInput") is Style inputStyle)
                state.TextBox.Style = inputStyle;

            dlg.Buttons = new ObservableCollection<DialogButton>
            {
                new()
                {
                    Text = LocalizationService.Instance["Dialog_Button_Cancel"],
                    StyleKey = "StyleCancel",
                    Command = new RelayCommand(() =>
                    {
                        tcs.TrySetResult(ToolResult.Canceled(Text("Tool_AskUser_Msg_Cancelled")));
                        dlg.Close();
                    }),
                },
                new()
                {
                    Text = LocalizationService.Instance["Dialog_Button_OK"],
                    StyleKey = "StyleBeige",
                    Command = new RelayCommand(() =>
                    {
                        var answer = (state.GetAnswer() ?? "").Trim();
                        if (answer.Length == 0)
                        {
                            tcs.TrySetResult(ToolResult.Error(Text("Tool_AskUser_Msg_Empty")));
                            dlg.Close();
                            return;
                        }

                        tcs.TrySetResult(ToolResult.Ok(Text("Tool_AskUser_Msg_Answer", answer)));
                        dlg.Close();
                    }),
                },
            };

            // 点击弹窗内下拉菜单以外的区域时关闭下拉。
            dlg.PreviewMouseDown += (_, e) =>
            {
                if (state.OptionsPopup is not { IsOpen: true } popup)
                    return;

                var source = e.OriginalSource as DependencyObject;
                if (IsInside(source, popup.PlacementTarget) || IsInside(source, popup.Child))
                    return;

                popup.IsOpen = false;
            };

            // 点击其他窗口 / 应用失去激活时，也关闭下拉。
            dlg.Deactivated += (_, _) =>
            {
                if (state.OptionsPopup is { IsOpen: true } popup)
                    popup.IsOpen = false;
            };

            // 用户直接点右上角 X 关闭也视为取消。
            dlg.Closed += (_, _) => tcs.TrySetResult(
                ToolResult.Canceled(Text("Tool_AskUser_Msg_Closed")));

            if (state.TextBox is not null)
                dlg.ContentRendered += (_, _) => state.TextBox.Focus();

            dialog = dlg;
            dlg.ShowDialog();
        }

        try
        {
            if (dispatcher.CheckAccess())
                ShowDialog();
            else
                dispatcher.Invoke(ShowDialog);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error(
                Text("Tool_AskUser_Msg_Failed", ex.Message)));
        }

        // 弹窗已关闭（回答/取消/超时），停止内部计时器。
        timeoutCts.Cancel();

        return tcs.Task;
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value))
            return value.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }

    private static List<string> GetOptions(JsonElement args)
    {
        var options = new List<string>();
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty("options", out var array) ||
            array.ValueKind != JsonValueKind.Array)
            return options;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var text = item.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                options.Add(text);
        }

        return options;
    }

    /// <summary>问题 + 文本输入框（无选项）或设置页风格的下拉选择框（有选项）。</summary>
    private static UIElement BuildContent(string question, IReadOnlyList<string> options, InputState state)
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = question,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 12),
        });

        if (options.Count > 0)
        {
            var selected = options[0];

            var selectedText = new TextBlock
            {
                Text = selected,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var arrow = new TextBlock
            {
                Text = "▾",
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var triggerGrid = new Grid();
            triggerGrid.Children.Add(selectedText);
            triggerGrid.Children.Add(arrow);

            var trigger = new Border
            {
                Height = 40,
                Background = new SolidColorBrush(Color.FromRgb(0xD4, 0xD0, 0xBA)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 0, 10, 0),
                Cursor = Cursors.Hand,
                Child = triggerGrid,
            };

            var optionList = new StackPanel();
            var popup = new Popup
            {
                PlacementTarget = trigger,
                Placement = PlacementMode.Bottom,
                StaysOpen = true,
                AllowsTransparency = true,
                Child = new Border
                {
                    Background = Brushes.White,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(5),
                    MaxWidth = 350,
                    Child = new ScrollViewer
                    {
                        MaxHeight = 220,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = optionList,
                    },
                },
            };
            popup.SetBinding(FrameworkElement.WidthProperty,
                new Binding(nameof(FrameworkElement.ActualWidth)) { Source = trigger });

            foreach (var option in options)
            {
                var value = option;
                var item = new Border
                {
                    Padding = new Thickness(10, 8, 10, 8),
                    CornerRadius = new CornerRadius(6),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = option,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                item.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    selected = value;
                    selectedText.Text = value;
                    popup.IsOpen = false;
                };
                optionList.Children.Add(item);
            }

            trigger.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                popup.IsOpen = !popup.IsOpen;
            };

            var host = new Grid();
            host.Children.Add(trigger);
            host.Children.Add(popup);
            panel.Children.Add(host);

            state.OptionsPopup = popup;
            state.GetAnswer = () => selected;
        }
        else
        {
            var textBox = new TextBox();
            state.TextBox = textBox;
            state.GetAnswer = () => textBox.Text;
            panel.Children.Add(textBox);
        }

        return panel;
    }

    /// <summary>判断元素是否位于指定祖先（或其后代）内。</summary>
    private static bool IsInside(DependencyObject? source, DependencyObject? ancestor)
    {
        if (source is null || ancestor is null)
            return false;

        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor))
                return true;

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private static Window? GetOwner()
        => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
           ?? Application.Current?.MainWindow;
}
