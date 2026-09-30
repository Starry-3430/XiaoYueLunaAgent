using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
                _viewModel.SelectSessionCommand.Execute(session);
        };

        _viewModel.Messages.CollectionChanged += (_, _) =>
        {
            MessageScrollViewer.Dispatcher.BeginInvoke(() =>
                MessageScrollViewer.ScrollToEnd());
        };

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.InputText))
                ChatTitle.Text = string.IsNullOrWhiteSpace(_viewModel.InputText) ? "新聊天" : _viewModel.InputText;
        };
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