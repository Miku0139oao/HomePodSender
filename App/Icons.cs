using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace HomePodSender;

/// <summary>运行时绘制托盘/窗口图标：圆底 + AirPlay 样式的弧线与三角，颜色表示状态</summary>
static class Icons
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    public static readonly Color Gray = Color.FromArgb(127, 140, 141);
    public static readonly Color Orange = Color.FromArgb(243, 156, 18);
    public static readonly Color Green = Color.FromArgb(39, 174, 96);
    public static readonly Color Red = Color.FromArgb(192, 57, 43);

    static readonly Dictionary<int, Icon> Cache = new();

    public static Icon Make(Color c)
    {
        if (Cache.TryGetValue(c.ToArgb(), out var ic)) return ic;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(c);
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, 9, 8, 14, 12, 200, 140);
            g.DrawArc(pen, 5.5f, 4.5f, 21, 19, 205, 130);
            using var white = new SolidBrush(Color.White);
            g.FillPolygon(white, new[] { new PointF(16, 15), new PointF(23, 25.5f), new PointF(9, 25.5f) });
        }
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        Cache[c.ToArgb()] = icon;
        return icon;
    }
}
