using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Luna.ViewModels;

namespace Luna;

public partial class HomeWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_ROUNDSMALL = 3;

    private readonly HomeViewModel _viewModel;
    private const int ResizeBorder = 6;
    private const int MaxToasts = 6;
    private const int ToastGap = 2;
    private const double SlideInDistance = 40;
    private const double AnimDuration = 0.3;
    private bool _isSettingsMode;

    private readonly List<ToastEntry> _toastStack = [];
    private int _toastIdSeq;

    public enum ToastType { Success, Warning, Error }

    private class ToastEntry
    {
        public int Id { get; set; }
        public Border Element { get; set; } = null!;
        public TranslateTransform Transform { get; set; } = null!;
        public DispatcherTimer? AutoDismissTimer { get; set; }
        public double Height { get; set; }
    }

    public HomeWindow(HomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        };

        HistoryList.SelectionChanged += (_, _) =>
        {
            if (HistoryList.SelectedItem is ChatSessionItem session)
            {
                _viewModel.SelectSessionCommand.Execute(session);
                ChatTitle.Text = session.Title;
                SwitchToChat();
            }
        };

        _viewModel.Messages.CollectionChanged += (_, _) =>
        {
            MessageScrollViewer.Dispatcher.BeginInvoke(() =>
                MessageScrollViewer.ScrollToEnd());
        };

        SettingsButton.Click += (_, _) => ShowSideContent(SettingsContent, "设置");
        DiaryButton.Click += (_, _) => ShowSideContent(DiaryContent, "日记本");
        ToolsButton.Click += (_, _) => ShowSideContent(ToolsContent, "工具");
        NewChatButton.Click += (_, _) => { SwitchToChat(); ChatTitle.Text = "新聊天"; };
    }

    private void ShowSideContent(Grid contentGrid, string title)
    {
        _isSettingsMode = true;
        ChatTitle.Text = title;
        MessageScrollViewer.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = contentGrid == SettingsContent ? Visibility.Visible : Visibility.Collapsed;
        DiaryContent.Visibility = contentGrid == DiaryContent ? Visibility.Visible : Visibility.Collapsed;
        ToolsContent.Visibility = contentGrid == ToolsContent ? Visibility.Visible : Visibility.Collapsed;
        InputArea.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        HistoryList.SelectedIndex = -1;
    }

    private void SwitchToChat()
    {
        _isSettingsMode = false;
        MessageScrollViewer.Visibility = Visibility.Visible;
        SettingsContent.Visibility = Visibility.Collapsed;
        DiaryContent.Visibility = Visibility.Collapsed;
        ToolsContent.Visibility = Visibility.Collapsed;
        InputArea.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Visible;
        HistoryList.SelectedIndex = -1;
    }

    // 迷你toast通知
    public void ShowToast(string text, ToastType type)
    {
        var bgColor = type switch
        {
            ToastType.Success => Color.FromRgb(0x4C, 0xAF, 0x50),
            ToastType.Warning => Color.FromRgb(0xC8, 0x8F, 0x54),
            ToastType.Error => Color.FromRgb(0xE5, 0x39, 0x35),
            _ => Color.FromRgb(0xC8, 0x8F, 0x54),
        };

        var id = ++_toastIdSeq;
        var transform = new TranslateTransform(0, SlideInDistance);
        var entry = new ToastEntry { Id = id, Transform = transform };

        var toast = BuildToastElement(text, bgColor, transform, id, toastId =>
        {
            var entry = FindToastEntry(toastId);
            if (entry != null) DismissToast(entry, false);
        });
        entry.Element = toast;

        ToastStack.Children.Add(toast);

        // 测量实际高度
        toast.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        entry.Height = toast.DesiredSize.Height;

        _toastStack.Add(entry);

        // 移除超出上限的最旧通知（渐变消失）
        while (_toastStack.Count >= MaxToasts)
        {
            var oldest = _toastStack[0];
            _toastStack.RemoveAt(0);
            oldest.AutoDismissTimer?.Stop();
            AnimateOpacity(oldest.Element, 0, AnimDuration, 0);
            var elem = oldest.Element;
            _ = Task.Delay((int)(AnimDuration * 1000)).ContinueWith(_ =>
                Dispatcher.Invoke(() => ToastStack.Children.Remove(elem)));
        }

        // 重新计算所有通知的理想位置
        RecalculatePositions();

        // 新通知滑入（从下方到理想位置）
        AnimateTransform(transform, GetIdealY(entry), AnimDuration, 0);
        AnimateOpacity(toast, 1, AnimDuration, 0);

        // 自动消失计时
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DismissToast(entry, false);
        };
        timer.Start();
        entry.AutoDismissTimer = timer;
    }

    private void DismissToast(ToastEntry entry, bool immediate)
    {
        if (!_toastStack.Contains(entry)) return;

        entry.AutoDismissTimer?.Stop();

        if (immediate)
        {
            RemoveToast(entry);
            return;
        }

        AnimateOpacity(entry.Element, 0, AnimDuration, 0);

        _ = Task.Delay((int)(AnimDuration * 1000)).ContinueWith(_ =>
        {
            Dispatcher.Invoke(() =>
            {
                if (!_toastStack.Contains(entry)) return;
                var index = _toastStack.IndexOf(entry);
                RemoveToast(entry);
            });
        });
    }

    private void RemoveToast(ToastEntry entry)
    {
        var index = _toastStack.IndexOf(entry);
        if (index < 0) return;

        _toastStack.RemoveAt(index);
        ToastStack.Children.Remove(entry.Element);

        RecalculatePositions();
    }

    private void RecalculatePositions()
    {
        double yOffset = 0;
        for (var i = _toastStack.Count - 1; i >= 0; i--)
        {
            var entry = _toastStack[i];
            AnimateTransform(entry.Transform, -yOffset, AnimDuration, 0);
            yOffset += ToastGap;
        }
    }

    private double GetIdealY(ToastEntry entry)
    {
        var index = _toastStack.IndexOf(entry);
        if (index < 0) return 0;

        double yOffset = 0;
        for (var i = _toastStack.Count - 1; i > index; i--)
            yOffset += ToastGap;
        return -yOffset;
    }

private static Border BuildToastElement(string text, Color bgColor, TranslateTransform transform, int id, Action<int> onClose)
    {
        var closeBlock = new TextBlock
        {
            Text = "✕",
            Style = Application.Current.TryFindResource("ToastClose") as Style,
        };

        var innerGrid = new Grid();
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 184,
        };
        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = System.Windows.Media.Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };
        scrollViewer.Content = textBlock;
        Grid.SetColumn(scrollViewer, 0);
        innerGrid.Children.Add(scrollViewer);
        Grid.SetColumn(closeBlock, 1);
        innerGrid.Children.Add(closeBlock);

        var toast = new Border
        {
            Style = Application.Current.TryFindResource("ToastBorder") as Style,
            Background = new SolidColorBrush(bgColor),
            Child = innerGrid,
            RenderTransform = transform,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Opacity = 0,
        };

        closeBlock.MouseLeftButtonDown += (_, _) => onClose(id);

        return toast;
    }

    private ToastEntry? FindToastEntry(int id) => _toastStack.FirstOrDefault(t => t.Id == id);

    private static void AnimateTransform(TranslateTransform transform, double toY, double duration, double delay)
    {
        var anim = new DoubleAnimation(toY, TimeSpan.FromSeconds(duration))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new PowerEase { EasingMode = EasingMode.EaseInOut, Power = 2 },
        };
        transform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private static void AnimateOpacity(UIElement element, double to, double duration, double delay)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromSeconds(duration))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
        };
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            MaxButton.Content = "❐";
            RootGrid.Margin = new Thickness(ResizeBorder);
        }
        else
        {
            MaxButton.Content = "□";
            RootGrid.Margin = new Thickness(0);
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void HeaderArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        if (WindowState == WindowState.Maximized)
        {
            var mousePos = e.GetPosition(this);
            var screenPoint = PointToScreen(mousePos);

            WindowState = WindowState.Normal;

            Left = screenPoint.X - mousePos.X;
            Top = 0;
        }

        DragMove();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            if (_viewModel.SendCommand.CanExecute(null))
                _viewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}