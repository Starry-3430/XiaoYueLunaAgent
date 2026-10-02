﻿﻿using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Luna.Controls;
using Luna.Models;
using Luna.ViewModels;
using Luna.Services;
using Luna.ViewModels;
using System.Runtime.InteropServices;

namespace Luna;

/// <summary>
/// 主窗口：负责紧凑/展开两种形态、显示/隐藏动画、输入框自适应和消息滚动。
/// </summary>
public partial class MainWindow : Window
{
    // Windows 消息：DPI 改变、显示设置改变
    private const int WmDpiChanged = 0x02E0;
    private const int WmDisplayChange = 0x007E;
    
    // GetWindowLong/SetWindowLong 的索引：扩展窗口样式
    private const int GwExStyle = -20;
    
    // WS_EX_TOOLWINDOW：工具窗口样式，避免出现在 Alt+Tab / 任务栏
    private const int WsExToolWindow = 0x00000080;

    // 从 user32.dll 获取窗口扩展样式
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    // 从 user32.dll 设置窗口扩展样式
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
    private bool _hadConversation;
    private EventHandler? _renderingHandler;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        // 监听消息集合变化，并为已有消息挂上属性变化监听
        _viewModel.Messages.CollectionChanged += OnMessagesChanged;
        foreach (var message in _viewModel.Messages)
        {
            message.PropertyChanged += OnMessagePropertyChanged;
        }

        // 窗口初始化完成后设置 Hook、尺寸变化、可见性变化等
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
        IsVisibleChanged += OnIsVisibleChanged;
        
        // 为输入框挂载自定义右键菜单
        _ = new EditorContextMenu(CompactInputBox);
        _ = new EditorContextMenu(InputBox);

        Loaded += (_, _) =>
        {
            UpdatePlaceholderVisibility();
            UpdateCompactInputSize();
        };

        MessageScrollViewer.RequestBringIntoView += (_, e) => e.Handled = true;
    }

    private void MessageText_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
            _ = new BubbleContextMenu(tb);
    }

    private void MessageViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMarkdownViewer.Controls.MarkdownDocumentView viewer) return;

        _ = new BubbleContextMenu(viewer);

        viewer.LinkClicked += (_, args) =>
            Process.Start(new ProcessStartInfo(args.Url) { UseShellExecute = true });

        viewer.ApplyTheme(WpfMarkdownViewer.Rendering.MarkdownStyle.Dark with
        {
            Background = System.Windows.Media.Brushes.Transparent,
            SubtleForeground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE0, 0xCA)),
            EmSize = 15,
            ParagraphLineHeight = 1.4,
            HeadingScales = new[] { 1.5, 1.3, 1.2, 1.1, 1.05, 1.0 },
            CodeBlockBackground = new SolidColorBrush(Color.FromRgb(0x63, 0x57, 0x4F)),
            InlineCodeBackground = new SolidColorBrush(Color.FromRgb(0x63, 0x57, 0x4F)),
            Border = new SolidColorBrush(Color.FromRgb(0x63, 0x57, 0x4F)),
            QuoteBar = new SolidColorBrush(Color.FromRgb(0x63, 0x57, 0x4F)),
        });

        ApplyLightScrollBarStyle(viewer);

        if (viewer.DataContext is not ChatMessage msg) return;

        viewer.SetMarkdown(msg.Content);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        var dirty = false;
        var rendering = false;

        timer.Tick += (_, _) =>
        {
            if (!dirty || rendering) return;
            rendering = true;
            dirty = false;
            try { viewer.SetMarkdown(msg.Content); }
            catch { dirty = true; }
            finally { rendering = false; }
            ApplyLightScrollBarStyle(viewer);
        };

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName != nameof(ChatMessage.Content)) return;
            dirty = true;
            if (!timer.IsEnabled) timer.Start();
        };

        msg.PropertyChanged += handler;

        viewer.Unloaded += (_, _) =>
        {
            msg.PropertyChanged -= handler;
            timer.Stop();
        };
    }

    private void ReasoningScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (sv.DataContext is not ChatMessage msg) return;

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(ChatMessage.Reasoning))
                sv.ScrollToBottom();
        };
        msg.PropertyChanged += handler;
        sv.Unloaded += (_, _) => msg.PropertyChanged -= handler;
    }

    private static void ApplyLightScrollBarStyle(WpfMarkdownViewer.Controls.MarkdownDocumentView viewer)
    {
        viewer.Dispatcher.BeginInvoke(() =>
        {
            foreach (var sb in FindVisualChildren<ScrollBar>(viewer))
                ApplyScrollBarThumbBrush(sb, new SolidColorBrush(Color.FromRgb(0xE4, 0xE0, 0xCA)));
        }, DispatcherPriority.Loaded);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t)
                yield return t;
            foreach (var grandchild in FindVisualChildren<T>(child))
                yield return grandchild;
        }
    }

    private static void ApplyScrollBarThumbBrush(ScrollBar scrollBar, Brush brush)
    {
        scrollBar.ApplyTemplate();
        var thumb = scrollBar.Template?.FindName("Thumb", scrollBar) as Thumb;
        if (thumb == null) return;
        thumb.ApplyTemplate();
        if (thumb.Template.FindName("ThumbBorder", thumb) is Border border)
            border.Background = brush;
    }

    private void MarkdownViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            if (IsInsideCodeBlock(e.OriginalSource as DependencyObject))
                return;

            e.Handled = true;

            if (sender is not DependencyObject element) return;
            var scrollViewer = FindChildScrollViewer(element);
            if (scrollViewer == null) return;

            var offset = scrollViewer.HorizontalOffset - e.Delta;
            offset = Math.Max(0, Math.Min(offset, scrollViewer.ScrollableWidth));
            scrollViewer.ScrollToHorizontalOffset(offset);
        }
        else
        {
            e.Handled = true;
            var parentSv = FindAncestorScrollViewer(sender as DependencyObject);
            parentSv?.ScrollToVerticalOffset(parentSv.VerticalOffset - e.Delta);
        }
    }

    private static bool IsInsideCodeBlock(DependencyObject? element)
    {
        while (element != null)
        {
            if (element.GetType().Name == "CodeBlockView") return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private static ScrollViewer? FindChildScrollViewer(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv) return sv;
            var result = FindChildScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? child)
    {
        while (child != null)
        {
            child = VisualTreeHelper.GetParent(child);
            if (child is ScrollViewer sv) return sv;
        }
        return null;
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            Clipboard.SetText(msg.Content);
    }

    private async void RewriteMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            await _viewModel.RewriteMessageAsync(msg);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }

        // 让窗口不显示在任务栏和 Alt+Tab 中
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GwExStyle);
        SetWindowLong(hwnd, GwExStyle, exStyle | WsExToolWindow);

        Reposition();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // DPI 或显示器布局变化时重新计算窗口位置
        if (msg is WmDpiChanged or WmDisplayChange)
        {
            Reposition();
        }
        return IntPtr.Zero;
    }

    private void Reposition()
    {
        // 顶部居中，距屏幕顶部 40px
        WindowPositioner.PlaceTopCenter(this, 40);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
        {
            // 隐藏时停止动画，避免后台继续渲染
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

        // 等布局完成后再播放显示动画，并聚焦紧凑输入框
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
                // 布局尺寸不可用时直接显示，避免窗口卡在透明/裁剪状态
                CompactContent.Opacity = 1;
                IslandBorder.Clip = null;
            }

            CompactInputBox.Focus();
        }, DispatcherPriority.Loaded);
    }

    // ===== 紧凑模式：输入自适应 =====
    

    private void CompactInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 文本变化时：更新占位符、输入框尺寸，并保证光标可见
        UpdatePlaceholderVisibility();
        UpdateCompactInputSize();
        EnsureCaretVisible();
    }

    private void UpdatePlaceholderVisibility()
    {
        // 输入为空时显示占位符
        var isEmpty = string.IsNullOrEmpty(CompactInputBox.Text);
        Placeholder.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCompactInputSize()
    {
        var text = CompactInputBox.Text;
        var displayText = string.IsNullOrEmpty(text) ? Placeholder.Text : text;

        // 使用当前输入框字体测量显示文本宽度
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

        // 预留左右各 12px 的空间
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
            CompactInputBox.Height = double.NaN; // 高度由内容自适应
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 新消息加入时订阅属性变化，旧消息移除时取消订阅
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

    // 保持视图滚动到底部
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
        // 延迟到布局完成后滚动
        Dispatcher.BeginInvoke(() => MessageScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void EnsureCaretVisible()
    {
        // 仅在输入框有键盘焦点时处理
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

            // 如果光标在最后一行或不在可见范围内，则滚动到对应行
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
        _hadConversation = _viewModel.Messages.Count > 0;
        StopAnimation();
        PrepareForShow();
        Opacity = 0;
        Left = -32000;
        Top = -32000;
        _isShown = false;
    }

    public void ShowInternal()
    {
        if (_hadConversation)
        {
            _viewModel.NewChat();
            SwitchToCompact();
            _hadConversation = false;
        }

        PrepareForShow();
        Opacity = 1;
        Reposition();
        _isShown = true;

        // 等布局完成后再播放动画，并聚焦输入框
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

    // public void ToggleVisibility()
    // {
    //     // 已显示则隐藏，否则显示
    //     if (_isShown)
    //         HideInternal();
    //     else
    //         ShowInternal();
    // }

    private void StopAnimation()
    {
        // 停止逐帧动画并清理裁剪，避免影响后续布局
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

        var baseH = IslandBorder.MinHeight;

        // 从中心点、零尺寸开始裁剪，后续逐帧扩展
        var clipRect = new RectangleGeometry
        {
            Rect = new Rect(targetW / 2, baseH / 2, 0, 0),
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

            // 第一阶段：从中心扩张到约 2/3 基础高度，圆形缓动
            if (elapsed < 0.2)
            {
                var t = elapsed / 0.2;
                var eased = CircleEaseOut(t);
                var size = baseH * 2.0 / 3.0 * eased;
                w = size;
                h = size;
            }
            // 第二阶段：从初始尺寸扩展到目标宽高
            else if (elapsed < 0.35)
            {
                var t = (elapsed - 0.2) / 0.15;
                var eased = CubicEaseOut(t);
                var startSize = baseH * 2.0 / 3.0;
                w = startSize + (targetW - startSize) * eased;
                h = startSize + (targetH - startSize) * eased;
            }
            // 结束阶段：固定为目标尺寸
            else
            {
                w = targetW;
                h = targetH;
            }

            w = Math.Max(w, 0);
            h = Math.Max(h, 0);

            // 根据动画进度计算裁剪矩形，并让圆角随高度变化，最大 25px
            if (clipRect != null)
            {
                var animT = elapsed < 0.2 ? 0 : Math.Min((elapsed - 0.2) / 0.15, 1.0);
                var refH = baseH + (targetH - baseH) * animT;
                var x = (targetW - w) / 2;
                var y = (refH - h) / 2;
                clipRect.Rect = new Rect(x, y, w, h);
                var r = Math.Min(h / 2, 25);
                clipRect.RadiusX = r;
                clipRect.RadiusY = r;
            }

            // 透明度：前 0.3 秒隐藏，0.3~0.8 秒渐显，之后完全显示
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

            // 动画结束：停止逐帧回调，并设置最终圆角
            if (elapsed >= 0.8)
            {
                StopAnimation();
                IslandBorder.CornerRadius = new CornerRadius(Math.Min(IslandBorder.ActualHeight / 2.0, 25));
            }
        };

        CompositionTarget.Rendering += _renderingHandler;
    }

    private static double CircleEaseOut(double t)
    {
        // 圆形缓出：先快后慢
        t -= 1;
        return Math.Sqrt(1 - t * t);
    }

    private static double CubicEaseOut(double t)
    {
        // 三次缓出：1 - (1 - t)^3
        return 1 - Math.Pow(1 - t, 3);
    }

    // ===== 模式切换 =====

    private void SwitchToCompact()
    {
        // 重新计算占位符与输入框尺寸
        StopAnimation();
        ExpandedContent.Visibility = Visibility.Collapsed;
        CompactContent.Visibility = Visibility.Visible;
        // CompactContent.Opacity = 1;
        UpdatePlaceholderVisibility();
        UpdateCompactInputSize();
        Dispatcher.BeginInvoke(() =>
        {
            // 等布局完成后根据实际高度设置胶囊圆角
            IslandBorder.CornerRadius = new CornerRadius(Math.Min(IslandBorder.ActualHeight / 2.0, 25));
        }, DispatcherPriority.Loaded);
    }

    private void SwitchToExpanded()
    {
        StopAnimation();
        CompactContent.Visibility = Visibility.Collapsed;
        ExpandedContent.Opacity = 0;
        ExpandedContent.Visibility = Visibility.Visible;
        IslandBorder.UpdateLayout();
        Dispatcher.BeginInvoke(() =>
        {
            ExpandedContent.Opacity = 1;
            CompactContent.Opacity = 1;
            IslandBorder.CornerRadius = new CornerRadius(25);
            InputBox.Focus();
            ScrollMessagesToEnd();
        }, DispatcherPriority.Loaded);
    }

    // ===== 展开模式的输入框 =====

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter 发送，Shift+Enter 换行
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            if (DataContext is MainViewModel vm && vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    // ===== 隐藏逻辑 ===== （弃用）

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
        // 胶囊窗口失去激活状态时自动隐藏
        HideInternal();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Esc 隐藏窗口
        if (e.Key == Key.Escape)
        {
            HideInternal();
            e.Handled = true;
        }
    }
}