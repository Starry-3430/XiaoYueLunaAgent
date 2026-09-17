using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Luna.Services;
using Luna.ViewModels;

namespace Luna;

public partial class MainWindow : Window
{
    private const int WmDpiChanged = 0x02E0;
    private const int WmDisplayChange = 0x007E;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ToggleWindowRequested += (_, _) => ToggleVisibility();

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                Reposition();
            }
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
    
    // 顶部居中
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        CenterOnScreen();
    }

    private void CenterOnScreen()
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Left + (screen.Width - ActualWidth) / 2;
        Top = screen.Top + 10; // 距离顶部 10px
    }
    
    // 鼠标进出
    
    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        IslandBorder.Width = 360;
        IslandBorder.Height = 200;
        Width = 380;   // 留一点边距
        Height = 220;
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        IslandBorder.Width = 180;
        IslandBorder.Height = 40;
        Width = 200;
        Height = 60;
    }
    
    // 失焦隐藏
    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // 窗口失去焦点时隐藏
        Hide();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // 按下 Esc 键时隐藏
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }
    
    // 快捷键唤醒
    private void ToggleVisibility()
    {
        if (IsVisible)
            Hide();
        else
        {
            Show();
            Activate();
        }
    }
}
