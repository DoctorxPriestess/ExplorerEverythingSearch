using System.Drawing;
using System.Runtime.InteropServices;

namespace ExplorerEverythingSearch.App.Tray;

/// <summary>
/// Draws the notification area icon at runtime (magnifier glyph), so the repository contains no
/// binary assets and the icon is reproducible from source.
/// </summary>
public static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Creates the application icon; the caller owns it and should dispose it.</summary>
    public static Icon Create(int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var stroke = Math.Max(2f, size / 11f);
            var lensDiameter = size * 0.58f;
            var lensRect = new RectangleF(
                size * 0.10f + stroke / 2,
                size * 0.10f + stroke / 2,
                lensDiameter,
                lensDiameter);

            using var pen = new Pen(Color.FromArgb(255, 45, 127, 249), stroke)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            using var highlight = new SolidBrush(Color.FromArgb(60, 255, 255, 255));

            graphics.FillEllipse(highlight, lensRect);
            graphics.DrawEllipse(pen, lensRect);

            var handleStart = new PointF(lensRect.Right - stroke * 0.4f, lensRect.Bottom - stroke * 0.4f);
            var handleEnd = new PointF(size * 0.92f, size * 0.92f);
            graphics.DrawLine(pen, handleStart, handleEnd);
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon does not depend on the temporary HICON.
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
