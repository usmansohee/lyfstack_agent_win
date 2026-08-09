using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Media;
using Drawing = System.Drawing;
using MediaColor = System.Windows.Media.Color;

namespace LyfStack.Agent.Windows.UI;

internal static class TrayIconFactory
{
    public static Icon Create(bool connected)
    {
        var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            using var bg = new SolidBrush(Drawing.Color.FromArgb(255, 15, 23, 42));
            g.FillEllipse(bg, 2, 2, 28, 28);

            using var ring = new SolidBrush(Drawing.Color.FromArgb(255, 13, 148, 136));
            g.FillEllipse(ring, 7, 7, 18, 18);

            Drawing.Color dot = connected
                ? Drawing.Color.FromArgb(255, 34, 197, 94)
                : Drawing.Color.FromArgb(255, 148, 163, 184);
            using var status = new SolidBrush(dot);
            g.FillEllipse(status, 20, 20, 10, 10);
        }

        IntPtr handle = bitmap.GetHicon();
        return (Icon)Icon.FromHandle(handle).Clone();
    }

    public static ImageSource CreateWindowIcon()
    {
        using Icon icon = Create(connected: true);
        using var stream = new MemoryStream();
        icon.Save(stream);
        stream.Position = 0;

        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static SolidColorBrush Brush(MediaColor color) => new(color);
}
