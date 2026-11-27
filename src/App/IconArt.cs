using System.IO;
using System.Runtime.InteropServices;
using SD = System.Drawing;
using SDD = System.Drawing.Drawing2D;

namespace WirelessDeviceMonitor;

/// <summary>Draws the tray icons and the app icon at runtime. All artwork is original.</summary>
public static class IconArt
{
    /// <summary>A rounded coloured square with a number in it (the pinned-device tray icon).</summary>
    public static SD.Bitmap LevelBitmap(int size, string text, SD.Color bg, SD.Color fg)
    {
        var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.SmoothingMode = SDD.SmoothingMode.AntiAlias;
        g.TextRenderingHint = SD.Text.TextRenderingHint.AntiAliasGridFit;
        using (var path = Rounded(0, 0, size, size, size * 0.2f))
        using (var b = new SD.SolidBrush(bg))
            g.FillPath(b, path);

        float px = size * (text.Length >= 3 ? 0.52f : text.Length == 2 ? 0.68f : 0.78f);
        using var font = new SD.Font("Segoe UI", px, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
        using var fmt = (SD.StringFormat)SD.StringFormat.GenericTypographic.Clone();
        fmt.Alignment = SD.StringAlignment.Center;
        fmt.LineAlignment = SD.StringAlignment.Center;
        using var fb = new SD.SolidBrush(fg);
        g.DrawString(text, font, fb, new SD.RectangleF(0, -size * 0.03f, size, size), fmt);
        return bmp;
    }

    /// <summary>The app icon: a battery outline, partly filled, on a rounded coloured square.</summary>
    public static SD.Bitmap AppBitmap(int size, SD.Color accent)
    {
        var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.SmoothingMode = SDD.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = SDD.PixelOffsetMode.HighQuality;
        float s = size;
        using (var path = Rounded(0, 0, s, s, s * 0.22f))
        using (var b = new SDD.LinearGradientBrush(new SD.RectangleF(0, 0, s, s),
                   Lighten(accent, 0.18f), Darken(accent, 0.12f), 60f))
            g.FillPath(b, path);

        float bw = s * 0.60f, bh = s * 0.36f, bx = s * 0.16f, by = (s - bh) / 2f;
        float stroke = Math.Max(1f, s * 0.07f);
        using var pen = new SD.Pen(SD.Color.White, stroke) { LineJoin = SDD.LineJoin.Round };
        using (var body = Rounded(bx, by, bw, bh, s * 0.07f)) g.DrawPath(pen, body);
        using var white = new SD.SolidBrush(SD.Color.White);
        g.FillRectangle(white, bx + bw + stroke * 0.3f, by + bh * 0.3f, s * 0.07f, bh * 0.4f); // terminal
        float pad = stroke * 1.25f;
        float fillW = (bw - pad * 2) * 0.68f;
        if (fillW >= 1 && bh - pad * 2 >= 1)
            g.FillRectangle(white, bx + pad, by + pad, fillW, bh - pad * 2);
        return bmp;
    }

    public static IntPtr ToHicon(SD.Bitmap bmp) => bmp.GetHicon();

    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Writes a multi-size .ico file for the executable (run once by the developer build).</summary>
    public static void WriteIco(string path, SD.Color accent)
    {
        int[] sizes = [16, 20, 24, 32, 48, 64, 256];
        var pngs = sizes.Select(sz =>
        {
            using var bmp = AppBitmap(sz, accent);
            using var ms = new MemoryStream();
            bmp.Save(ms, SD.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }

    static SDD.GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new SDD.GraphicsPath();
        float d = Math.Max(0.5f, Math.Min(r * 2, Math.Min(w, h)));
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static SD.Color Lighten(SD.Color c, float t) => SD.Color.FromArgb(255,
        (int)(c.R + (255 - c.R) * t), (int)(c.G + (255 - c.G) * t), (int)(c.B + (255 - c.B) * t));
    static SD.Color Darken(SD.Color c, float t) => SD.Color.FromArgb(255,
        (int)(c.R * (1 - t)), (int)(c.G * (1 - t)), (int)(c.B * (1 - t)));
}
