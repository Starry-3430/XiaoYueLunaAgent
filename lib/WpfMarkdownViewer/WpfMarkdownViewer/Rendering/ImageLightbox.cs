using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WpfMarkdownViewer.Rendering;

/// <summary>Shows an image full-screen over a dim backdrop; click anywhere or press Esc to close.</summary>
internal static class ImageLightbox
{
    public static void Show(ImageSource image, FrameworkElement origin)
    {
        var picture = new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Margin = new Thickness(48),
        };
        var backdrop = new Grid { Background = new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0)) };
        backdrop.Children.Add(picture);

        var window = new Window
        {
            Content = backdrop,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            WindowState = WindowState.Maximized,
            ShowInTaskbar = false,
            Owner = Window.GetWindow(origin),
        };

        backdrop.MouseLeftButtonUp += (_, _) => window.Close();
        window.KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter or Key.Space) window.Close(); };
        window.Show();
    }
}
