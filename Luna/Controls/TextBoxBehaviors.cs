using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Luna.Controls;

/// <summary>
/// 文本框通用交互行为。
/// </summary>
public static class TextBoxBehaviors
{
    /// <summary>
    /// 为 true 时，文本框在按下回车后失去焦点（多行文本框除外）。
    /// </summary>
    public static readonly DependencyProperty EnterLosesFocusProperty =
        DependencyProperty.RegisterAttached(
            "EnterLosesFocus",
            typeof(bool),
            typeof(TextBoxBehaviors),
            new PropertyMetadata(false, OnEnterLosesFocusChanged));

    public static bool GetEnterLosesFocus(DependencyObject obj) =>
        (bool)obj.GetValue(EnterLosesFocusProperty);

    public static void SetEnterLosesFocus(DependencyObject obj, bool value) =>
        obj.SetValue(EnterLosesFocusProperty, value);

    private static void OnEnterLosesFocusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox) return;

        if (e.NewValue is true)
            textBox.PreviewKeyDown += TextBox_PreviewKeyDown;
        else
            textBox.PreviewKeyDown -= TextBox_PreviewKeyDown;
    }

    private static void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        if (e.Key != Key.Enter || textBox.AcceptsReturn) return;

        e.Handled = true;
        textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }
}
