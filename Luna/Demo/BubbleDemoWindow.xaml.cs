using System.Windows;
using Luna.Controls;

namespace Luna.Demo;

public partial class BubbleDemoWindow : Window
{
    private BubbleTipWindow? _bubble;

    public BubbleDemoWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => RebuildBubble();

        SideBox.SelectionChanged += (_, _) =>
        {
            if (IsLoaded) RebuildBubble();
        };

        ShowBubbleBox.Checked += (_, _) =>
        {
            if (IsLoaded) RebuildBubble();
        };

        ShowBubbleBox.Unchecked += (_, _) => CloseBubble();

        OffsetSlider.ValueChanged += (_, _) =>
        {
            ButtonOffset.X = OffsetSlider.Value;
        };

        Closed += (_, _) => CloseBubble();
    }

    private void RebuildBubble()
    {
        CloseBubble();

        if (ShowBubbleBox.IsChecked != true) return;

        var side = SideBox.SelectedIndex switch
        {
            0 => BubbleSide.Top,
            2 => BubbleSide.Left,
            3 => BubbleSide.Right,
            _ => BubbleSide.Bottom,
        };

        _bubble = new BubbleTipWindow(
            "管理员权限下，拖拽导入文件不可用。请使用“导入附件”按钮",
            AttachButton,
            side);
        _bubble.Show();
    }

    private void CloseBubble()
    {
        _bubble?.Close();
        _bubble = null;
    }
}
