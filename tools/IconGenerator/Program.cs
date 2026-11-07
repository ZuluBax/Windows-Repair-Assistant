using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGenerator;

/// <summary>
/// Renders the app icon (white wrench on the app's accent-blue rounded tile) from vector shapes
/// into a multi-resolution .ico. Drawn from our own geometry rather than an icon-font glyph so the
/// artwork is ours to distribute.
/// </summary>
internal static class Program
{
    private const double Canvas = 256;

    // Sizes Windows uses for taskbar, title bar, Explorer views, Start and Alt+Tab at common DPI scales.
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    [STAThread]
    private static int Main(string[] args)
    {
        string output = args.Length > 0
            ? args[0]
            : Path.Combine("src", "WindowsRepairAssistant", "Assets", "app.ico");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        // Classic DIB entries below 256 px, PNG only at 256 px: the layout Windows' own icons use.
        // An all-PNG .ico embedded via <ApplicationIcon> produced an assembly the runtime rejected
        // ("Bad IL format") and that antivirus quarantined.
        List<byte[]> images = Sizes.Select(size => size >= 256 ? RenderPng(size) : RenderDib(size)).ToList();
        WriteIco(output, Sizes, images);

        // Previews for visual review without an icon viewer (git-ignored, kept out of the app's Assets).
        string previewDir = Path.Combine("tools", "IconGenerator", "preview");
        Directory.CreateDirectory(previewDir);
        foreach (int size in new[] { 256, 48, 32, 16 })
        {
            File.WriteAllBytes(Path.Combine(previewDir, $"icon-{size}.png"), RenderPng(size));
        }
        Console.WriteLine($"Wrote {Path.GetFullPath(output)} ({string.Join(", ", Sizes)} px)");
        return 0;
    }

    private static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / Canvas, size / Canvas));
            DrawIcon(dc, size);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>
    /// ICO DIB entry: BITMAPINFOHEADER (height doubled for the XOR+AND masks), bottom-up
    /// straight-alpha BGRA pixels, then an all-zero AND mask (transparency comes from alpha).
    /// </summary>
    private static byte[] RenderDib(int size)
    {
        var straight = new FormatConvertedBitmap(Render(size), PixelFormats.Bgra32, null, 0);
        int stride = size * 4;
        var pixels = new byte[stride * size];
        straight.CopyPixels(pixels, stride, 0);

        int maskStride = ((size + 31) / 32) * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40);                // biSize
        writer.Write(size);              // biWidth
        writer.Write(size * 2);          // biHeight (image + mask)
        writer.Write((ushort)1);         // biPlanes
        writer.Write((ushort)32);        // biBitCount
        writer.Write(0);                 // biCompression = BI_RGB
        writer.Write(pixels.Length + maskStride * size);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);

        for (int row = size - 1; row >= 0; row--)
        {
            writer.Write(pixels, row * stride, stride);
        }

        writer.Write(new byte[maskStride * size]);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] RenderPng(int size)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(size)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void DrawIcon(DrawingContext dc, int pixelSize)
    {
        // Tiny sizes get less inset so the tile fills the slot and stays legible.
        double inset = pixelSize <= 24 ? 4 : 12;
        var tile = new Rect(inset, inset, Canvas - 2 * inset, Canvas - 2 * inset);
        double radius = tile.Width * 0.22;

        var tileBrush = new LinearGradientBrush(
            Color.FromRgb(0x1E, 0x88, 0xE0), Color.FromRgb(0x00, 0x5A, 0xB0), new Point(0, 0), new Point(0, 1));
        dc.DrawRoundedRectangle(tileBrush, null, tile, radius, radius);

        Geometry wrench = CreateWrench();
        // Soft shadow for depth at larger sizes; skipped where it would just blur.
        if (pixelSize >= 48)
        {
            var shadow = wrench.Clone();
            shadow.Transform = new TranslateTransform(0, 5);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x20, 0x50)), null, shadow);
        }

        dc.DrawGeometry(Brushes.White, null, wrench);
    }

    /// <summary>Open-ended wrench, built horizontally and rotated so the jaw points to the top-left.</summary>
    private static Geometry CreateWrench()
    {
        const double cy = Canvas / 2;
        // Head and handle lengths chosen so the rotated wrench is optically centred on the tile.
        var headCenter = new Point(90, cy);
        const double headRadius = 50;
        const double jawHalfWidth = 19;
        const double handleHalfWidth = 21;

        var head = new EllipseGeometry(headCenter, headRadius, headRadius);
        var handle = new RectangleGeometry(new Rect(headCenter.X, cy - handleHalfWidth, 218 - headCenter.X, handleHalfWidth * 2), handleHalfWidth, handleHalfWidth);
        var body = new CombinedGeometry(GeometryCombineMode.Union, head, handle);

        // The jaw: a slot cut from the outer edge of the head towards its centre.
        var jaw = new RectangleGeometry(new Rect(headCenter.X - headRadius - 10, cy - jawHalfWidth, headRadius + 12, jawHalfWidth * 2));
        var wrench = new CombinedGeometry(GeometryCombineMode.Exclude, body, jaw)
        {
            Transform = new RotateTransform(45, Canvas / 2, Canvas / 2),
        };

        PathGeometry flattened = wrench.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
        flattened.Freeze();
        return flattened;
    }

    /// <summary>Writes an ICO from pre-encoded entries (DIB or PNG).</summary>
    private static void WriteIco(string path, int[] sizes, List<byte[]> pngs)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0);            // reserved
        writer.Write((ushort)1);            // type: icon
        writer.Write((ushort)sizes.Length); // image count

        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // 0 means 256
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)0);   // palette colours
            writer.Write((byte)0);   // reserved
            writer.Write((ushort)1); // colour planes
            writer.Write((ushort)32);// bits per pixel
            writer.Write(pngs[i].Length);
            writer.Write(offset);
            offset += pngs[i].Length;
        }

        foreach (byte[] png in pngs)
        {
            writer.Write(png);
        }
    }
}
