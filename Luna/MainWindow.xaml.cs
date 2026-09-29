using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
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
using System.Runtime.InteropServices;

namespace Luna;

public partial class MainWindow : Window
{
    private const int WmDpiChanged = 0x02E0;
    private const int WmDisplayChange = 0x007E;
    private const int GwExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // 紧凑输入框尺寸约束
    private const double MinCompactWidth = 156;
    private const double MaxCompactWidth = 420;
    private const double LineHeight = 18;
    private const int MaxLines = 5;
    private const double VerticalPadding = 12;

    private readonly MainViewModel _viewModel;
    private readonly Stopwatch _animationStopwatch = new();
    private bool _animating;
    private bool _isShown;
    private EventHandler? _renderingHandler;

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

        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GwExStyle);
        SetWindowLong(hwnd, GwExStyle, exStyle | WsExToolWindow);

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
        WindowPositioner.PlaceTopCenter(this, 40);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
        {
            StopAnimation();
            return;
        }

        // 同步设置初始状态，避免 HWND 重建后首帧闪现完整窗口
        CompactContent.Opacity = 0;
        IslandBorder.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, 0, 0),
            RadiusX = 0,
            RadiusY = 0
        };

        Reposition();

        Dispatcher.BeginInvoke(() =>
        {
            var targetW = IslandBorder.ActualWidth;
            var targetH = IslandBorder.ActualHeight;

            if (targetW > 0 && targetH > 0)
            {
                PlayShowAnimation(targetW, targetH);
            }
            else
            {
                CompactContent.Opacity = 1;
                IslandBorder.Clip = null;
            }

            CompactInputBox.Focus();
        }, DispatcherPriority.Loaded);
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
    
    

    // ===== 唤起动画 =====
    
    // 预备
    private void PrepareForShow()
    {
        // 强制内容透明 + 零尺寸裁剪，且不依赖布局尺寸
        CompactContent.Opacity = 0;
        IslandBorder.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, 0, 0),
            RadiusX = 0,
            RadiusY = 0
        };
    }
    
    // 隐藏时也调用
    
private void HideInternal()
    {
        StopAnimation();
        PrepareForShow();
        Opacity = 0;
        Left = -32000;
        Top = -32000;
        _isShown = false;
    }

    public void ShowInternal()
    {
        PrepareForShow();
        Opacity = 1;
        Reposition();
        _isShown = true;

        Dispatcher.BeginInvoke(() =>
        {
            var targetW = IslandBorder.ActualWidth;
            var targetH = IslandBorder.ActualHeight;

            if (targetW > 0 && targetH > 0)
            {
                PlayShowAnimation(targetW, targetH);
            }
            else
            {
                CompactContent.Opacity = 1;
                IslandBorder.Clip = null;
            }

            CompactInputBox.Focus();
        }, DispatcherPriority.Loaded);
    }

    public void ToggleVisibility()
    {
        if (_isShown)
            HideInternal();
        else
            ShowInternal();
    }

    private void StopAnimation()
    {
        _animating = false;
        _animationStopwatch.Reset();
        if (_renderingHandler != null)
        {
            CompositionTarget.Rendering -= _renderingHandler;
            _renderingHandler = null;
        }
        IslandBorder.Clip = null;
    }

    private void PlayShowAnimation(double targetW, double targetH)
    {
        if (_animating) return;
        if (targetW <= 0 || targetH <= 0) return;

        var clipRect = new RectangleGeometry
        {
            Rect = new Rect(targetW / 2, targetH / 2, 0, 0),
            RadiusX = 0,
            RadiusY = 0
        };
        IslandBorder.Clip = clipRect;

        _animationStopwatch.Restart();
        _animating = true;

        _renderingHandler = (_, _) =>
        {
            var elapsed = _animationStopwatch.Elapsed.TotalSeconds;

            double w, h;

            if (elapsed < 0.2)
            {
                var t = elapsed / 0.2;
                var eased = CircleEaseOut(t);
                var size = targetH * 2.0 / 3.0 * eased;
                w = size;
                h = size;
            }
            else if (elapsed < 0.35)
            {
                var t = (elapsed - 0.2) / 0.15;
                var eased = CubicEaseOut(t);
                var startSize = targetH * 2.0 / 3.0;
                w = startSize + (targetW - startSize) * eased;
                h = startSize + (targetH - startSize) * eased;
            }
            else if (elapsed < 0.5)
            {
                var t = (elapsed - 0.35) / 0.15;
                var bounce = Math.Sin(t * Math.PI * 2) * (1 - t) * 0.04;
                w = targetW * (1 + bounce);
                h = targetH * (1 + bounce);
            }
            else
            {
                w = targetW;
                h = targetH;
            }

            w = Math.Max(w, 0);
            h = Math.Max(h, 0);

            if (clipRect != null)
            {
                var x = (targetW - w) / 2;
                var y = (targetH - h) / 2;
                clipRect.Rect = new Rect(x, y, w, h);
                clipRect.RadiusX = h / 2;
                clipRect.RadiusY = h / 2;
            }

            if (elapsed <= 0.3)
            {
                CompactContent.Opacity = 0;
            }
            else if (elapsed < 0.8)
            {
                CompactContent.Opacity = (elapsed - 0.3) / 0.5;
            }
            else
            {
                CompactContent.Opacity = 1;
            }

            if (elapsed >= 0.8)
            {
                StopAnimation();
                IslandBorder.CornerRadius = new CornerRadius(IslandBorder.ActualHeight / 2.0);
            }
        };

        CompositionTarget.Rendering += _renderingHandler;
    }

    private static double CircleEaseOut(double t)
    {
        t -= 1;
        return Math.Sqrt(1 - t * t);
    }

    private static double CubicEaseOut(double t)
    {
        return 1 - Math.Pow(1 - t, 3);
    }

    // ===== 模式切换 =====

    private void SwitchToCompact()
    {
        StopAnimation();
        ExpandedContent.Visibility = Visibility.Collapsed;
        CompactContent.Visibility = Visibility.Visible;
        // CompactContent.Opacity = 1;
        UpdatePlaceholderVisibility();
        UpdateCompactInputSize();
        Dispatcher.BeginInvoke(() =>
        {
            IslandBorder.CornerRadius = new CornerRadius(IslandBorder.ActualHeight / 2.0);
        }, DispatcherPriority.Loaded);
    }

    private void SwitchToExpanded()
    {
        StopAnimation();
        CompactContent.Visibility = Visibility.Collapsed;
        // CompactContent.Opacity = 1;
        ExpandedContent.Visibility = Visibility.Visible;
        IslandBorder.CornerRadius = new CornerRadius(25);
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
        HideInternal();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideInternal();
            e.Handled = true;
        }
    }
}