using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TempMon.Desktop.Tray;

/// <summary>
/// Programmatic tray/logo bitmap — ships no .ico asset. Draws a rounded-rect gradient
/// (the dashboard's #60CDFF → #4CC38A) into a <see cref="RenderTargetBitmap"/> and hands it to
/// <c>TaskbarIcon.IconSource</c>, which takes a WPF <see cref="ImageSource"/>. Rendered large and
/// downscaled by Windows for crispness at high DPI; the result is frozen and cached.
/// </summary>
internal static class TrayIcon
{
    private static ImageSource? _cached;

    public static ImageSource BuildLogo(int px = 32)
    {
        // Reuse one frozen instance for the default size — built once, cross-thread safe.
        if (px == 32 && _cached is not null) return _cached;

        var gradient = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString("#60CDFF"),
            (Color)ColorConverter.ConvertFromString("#4CC38A"),
            new Point(0, 0), new Point(1, 1));
        gradient.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double radius = px * 0.28;
            dc.DrawRoundedRectangle(gradient, null,
                new Rect(0, 0, px, px), radius, radius);
        }

        var bitmap = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        if (px == 32) _cached = bitmap;
        return bitmap;
    }
}
