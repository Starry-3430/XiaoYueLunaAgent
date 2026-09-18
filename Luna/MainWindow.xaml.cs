using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Luna.Models;
using Luna.Services;
using Luna.ViewModels;

namespace Luna;

public partial class MainWindow : Window
{
    private const int WmDpiChanged = 0x02E0;
    private const int WmDisplayChange = 0x007E;

    // 紧凑输入框尺寸约束
    private const double MinCompactWidth = 156;
    private const double MaxCompactWidth = 420;
    private const double LineHeight = 18;
    private const int MaxLines = 5;
    private const double VerticalPadding = 12;

    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        _viewModel.Messages.CollectionChanged += OnMessagesChanged;
        foreach (var message in _viewModel.Messages)
        {
            message.PropertyChanged += OnMessagePropertyChanged;
        }

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
        IsVisibleChanged += OnIsVisibleChanged;
        Loaded += (_, _) =>
        {
            UpdatePlaceholderVisibility();
            UpdateCompactInputSize();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
        Reposition();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmDpiChanged or WmDisplayChange)
        {
            Reposition();
        }
        return IntPtr.Zero;
    }

    private void Reposition()
    {
        if (IsVisible)
        {
            WindowPositioner.PlaceTopCenter(this);
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            // 每次显示都回到紧凑模式
            SwitchToCompact();
            Reposition();
            Dispatcher.BeginInvoke(() => CompactInputBox.Focus(), DispatcherPriority.Input);
        }
    }

    // ===== 紧凑模式：输入自适应 =====
    

    private void CompactInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholderVisibility();
        UpdateCompactInputSize();
        EnsureCaretVisible();
    }

    private void UpdatePlaceholderVisibility()
    {
        var isEmpty = string.IsNullOrEmpty(CompactInputBox.Text);
        Placeholder.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCompactInputSize()
    {
        var text = CompactInputBox.Text;
        var displayText = string.IsNullOrEmpty(text) ? Placeholder.Text : text;

        var typeface = new Typeface(
            CompactInputBox.FontFamily,
            CompactInputBox.FontStyle,
            CompactInputBox.FontWeight,
            CompactInputBox.FontStretch);

        var dpi = VisualTreeHelper.GetDpi(CompactInputBox).PixelsPerDip;

        var ft = new FormattedText(
            displayText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            CompactInputBox.FontSize,
            Brushes.White,
            dpi);

        double desiredWidth = ft.Width + 24;
        var hasNewLine = text.Contains('\r') || text.Contains('\n');
        var multiLine = hasNewLine || desiredWidth > MaxCompactWidth;

        if (!multiLine)
        {
            // 单行阶段
            CompactInputBox.Width = Math.Max(MinCompactWidth, desiredWidth);
            CompactInputBox.Padding = new Thickness(6, 4, 6, 4);
            CompactInputBox.TextWrapping = TextWrapping.NoWrap;
            CompactInputBox.VerticalContentAlignment = VerticalAlignment.Center;
            CompactInputBox.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            CompactInputBox.MaxHeight = double.PositiveInfinity;
            CompactInputBox.Height = LineHeight + VerticalPadding;
        }
        else
        {
            // 多行阶段：宽度至少填满胶囊内容区，右侧不留内边距，使滚动条贴在最右边
            var minContentWidth = Math.Max(MinCompactWidth,
                IslandBorder.MinWidth - IslandBorder.Padding.Left - IslandBorder.Padding.Right);

            CompactInputBox.Width = Math.Min(MaxCompactWidth, Math.Max(minContentWidth, desiredWidth));
            CompactInputBox.Padding = new Thickness(6, 4, 0, 4);
            CompactInputBox.TextWrapping = TextWrapping.Wrap;
            CompactInputBox.VerticalContentAlignment = VerticalAlignment.Top;
            CompactInputBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            CompactInputBox.MaxHeight = LineHeight * MaxLines + VerticalPadding;
            CompactInputBox.Height = double.NaN;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (ChatMessage message in e.NewItems)
            {
                message.PropertyChanged += OnMessagePropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (ChatMessage message in e.OldItems)
            {
                message.PropertyChanged -= OnMessagePropertyChanged;
            }
        }

        ScrollMessagesToEnd();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessage.Content) or nameof(ChatMessage.Role))
        {
            ScrollMessagesToEnd();
        }
    }

    // ===== 滚动逻辑 =====
    // 发送消息的时候消息区滚动到最底部
    private void ScrollMessagesToEnd()
    {
        Dispatcher.BeginInvoke(() => MessageScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void EnsureCaretVisible()
    {
        if (!CompactInputBox.IsKeyboardFocusWithin)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            var caretLine = CompactInputBox.GetLineIndexFromCharacterIndex(CompactInputBox.CaretIndex);
            if (caretLine < 0)
            {
                return;
            }

            if (caretLine >= CompactInputBox.LineCount - 1)
            {
                CompactInputBox.ScrollToEnd();
            }
            else if (caretLine < CompactInputBox.GetFirstVisibleLineIndex() ||
                     caretLine > CompactInputBox.GetLastVisibleLineIndex())
            {
                CompactInputBox.ScrollToLine(caretLine);
            }
        }, DispatcherPriority.Background);
    }

    private void CompactInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Shift+Enter 仍然换行，不触发发送
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            if (DataContext is not MainViewModel vm)
            {
                return;
            }

            // 空内容不发送
            if (string.IsNullOrWhiteSpace(vm.InputText))
            {
                e.Handled = true;
                return;
            }

            // 1. 先切到展开模式
            SwitchToExpanded();

            // 2. 再触发发送
            if (vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
            }

            e.Handled = true;
        }
    }
    
    

    // ===== 模式切换 =====

    private void SwitchToCompact()
    {
        ExpandedContent.Visibility = Visibility.Collapsed;
        CompactContent.Visibility = Visibility.Visible;
        UpdatePlaceholderVisibility();
        UpdateCompactInputSize();
    }

    private void SwitchToExpanded()
    {
        CompactContent.Visibility = Visibility.Collapsed;
        ExpandedContent.Visibility = Visibility.Visible;
        InputBox.Focus();
        ScrollMessagesToEnd();
    }

    // ===== 展开模式的输入框 =====

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            if (DataContext is MainViewModel vm && vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    // ===== 隐藏逻辑 =====

    // private void Window_MouseLeave(object sender, MouseEventArgs e)
    // {
    //     // 正在输入时，鼠标离开不隐藏
    //     if (DataContext is MainViewModel vm && !string.IsNullOrEmpty(vm.InputText))
    //     {
    //         return;
    //     }
    //
    //     Hide();
    // }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        Hide();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }
}