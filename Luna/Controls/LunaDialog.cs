// 通用自定义弹窗：圆角、可拖动、按钮动态生成、样式键切换

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Luna.Models;

namespace Luna.Controls;

public partial class LunaDialog : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static readonly DependencyProperty DialogTitleProperty =
        DependencyProperty.Register(nameof(DialogTitle), typeof(string), typeof(LunaDialog),
            new PropertyMetadata(null, OnTitleChanged));

    public static readonly DependencyProperty DialogContentProperty =
        DependencyProperty.Register(nameof(DialogContent), typeof(object), typeof(LunaDialog),
            new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty ButtonsProperty =
        DependencyProperty.Register(nameof(Buttons), typeof(ObservableCollection<DialogButton>), typeof(LunaDialog),
            new PropertyMetadata(null, OnButtonsChanged));

    public string? DialogTitle
    {
        get => (string?)GetValue(DialogTitleProperty);
        set => SetValue(DialogTitleProperty, value);
    }

    public object? DialogContent
    {
        get => GetValue(DialogContentProperty);
        set => SetValue(DialogContentProperty, value);
    }

    public ObservableCollection<DialogButton>? Buttons
    {
        get => (ObservableCollection<DialogButton>?)GetValue(ButtonsProperty);
        set => SetValue(ButtonsProperty, value);
    }

    public LunaDialog()
    {
        InitializeComponent();
        Buttons = [];

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        };
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dialog = (LunaDialog)d;
        dialog.TitleBlock.Text = e.NewValue as string;
        dialog.TitleBlock.Visibility = e.NewValue is string s && !string.IsNullOrEmpty(s)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dialog = (LunaDialog)d;
        dialog.ContentArea.Content = e.NewValue;
    }

    /// <summary>按住按钮以外的任意位置拖动弹窗。</summary>
    private void Dialog_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;

        DragMove();
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Button) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private static void OnButtonsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dialog = (LunaDialog)d;
        if (e.OldValue is ObservableCollection<DialogButton> oldCollection)
            oldCollection.CollectionChanged -= dialog.ButtonsOnCollectionChanged;
        if (e.NewValue is ObservableCollection<DialogButton> newCollection)
            newCollection.CollectionChanged += dialog.ButtonsOnCollectionChanged;
        dialog.RebuildButtons();
    }

    private void ButtonsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildButtons();
    }

    private void RebuildButtons()
    {
        Dispatcher.InvokeAsync(() =>
        {
            var buttons = Buttons;
            if (buttons is null || buttons.Count == 0)
            {
                ButtonsPanel.Content = null;
                return;
            }

            ButtonsPanel.Margin = new Thickness(0, 0, 0, 10);

            if (buttons.Count == 1)
            {
                // 单个按钮铺满弹窗，长度与多按钮时一致，保证英文完整显示
                var btn = MakeButton(buttons[0]);
                btn.HorizontalAlignment = HorizontalAlignment.Stretch;
                ButtonsPanel.Content = btn;
            }
            else if (buttons.Count == 2)
            {
                // 两个按钮等宽铺满弹窗，保证英文等较长文案能完整显示
                var grid = new Grid();
                for (var i = 0; i < 2; i++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                for (var i = 0; i < 2; i++)
                {
                    var btn = MakeButton(buttons[i]);
                    btn.Margin = i == 0 ? new Thickness(0, 0, 5, 0) : new Thickness(5, 0, 0, 0);
                    Grid.SetColumn(btn, i);
                    grid.Children.Add(btn);
                }

                ButtonsPanel.Content = grid;
            }
            else
            {
                // 三个及以上按钮纵向铺满弹窗，避免较长英文文案被截断
                var panel = new StackPanel { Orientation = Orientation.Vertical };
                foreach (var button in buttons)
                {
                    var btn = MakeButton(button);
                    btn.HorizontalAlignment = HorizontalAlignment.Stretch;
                    btn.Margin = new Thickness(0, 0, 0, 8);
                    panel.Children.Add(btn);
                }

                ButtonsPanel.Content = panel;
            }
        });
    }

    private Button MakeButton(DialogButton model)
    {
        var styleKey = !string.IsNullOrEmpty(model.StyleKey) ? model.StyleKey :
                       model.IsPrimary ? "StylePrimary" :
                       model.IsCancel ? "StyleCancel" : "StyleDefault";

        return new Button
        {
            Content = model.Text,
            Command = model.Command,
            CommandParameter = model.CommandParameter,
            Style = TryFindResource(styleKey) as Style
        };
    }
}