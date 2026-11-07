using System.Globalization;
using System.Text;

namespace WindowsRepairAssistant.Core;

/// <summary>
/// Decodes console output from DISM/SFC and splits it into lines in real time.
/// </summary>
public static class ConsoleOutputReader
{
    // Enough bytes to reliably tell UTF-16 (every other byte zero for Latin text) from a single-byte code page.
    private const int DetectionThreshold = 64;

    static ConsoleOutputReader()
    {
        // OEM code pages (e.g. 437, 850) are not available in .NET by default.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Reads the stream to the end, invoking <paramref name="onLine"/> per non-empty line, and returns all text.</summary>
    public static async Task<string> ReadAllAsync(Stream stream, Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onLine);

        var splitter = new LineSplitter(onLine);
        var buffer = new byte[4096];
        using var pending = new MemoryStream();
        Decoder? decoder = null;

        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (decoder is null)
            {
                pending.Write(buffer, 0, read);
                if (pending.Length < DetectionThreshold)
                {
                    continue;
                }

                decoder = DetectEncoding(pending.GetBuffer().AsSpan(0, (int)pending.Length)).GetDecoder();
                Decode(decoder, pending.GetBuffer(), (int)pending.Length, splitter, flush: false);
            }
            else
            {
                Decode(decoder, buffer, read, splitter, flush: false);
            }
        }

        if (decoder is null)
        {
            decoder = DetectEncoding(pending.GetBuffer().AsSpan(0, (int)pending.Length)).GetDecoder();
            Decode(decoder, pending.GetBuffer(), (int)pending.Length, splitter, flush: true);
        }
        else
        {
            Decode(decoder, [], 0, splitter, flush: true);
        }

        splitter.Complete();
        return splitter.AllText;
    }

    internal static Encoding DetectEncoding(ReadOnlySpan<byte> sample)
    {
        if (sample.Length >= 2 && sample[0] == 0xFF && sample[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        int pairs = sample.Length / 2;
        if (pairs > 0)
        {
            int zeroHighBytes = 0;
            for (int i = 1; i < pairs * 2; i += 2)
            {
                if (sample[i] == 0)
                {
                    zeroHighBytes++;
                }
            }

            if (zeroHighBytes * 10 >= pairs * 4)
            {
                return Encoding.Unicode;
            }
        }

        return GetOemEncoding();
    }

    private static Encoding GetOemEncoding()
    {
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    private static void Decode(Decoder decoder, byte[] bytes, int count, LineSplitter splitter, bool flush)
    {
        // +2 covers a byte carried over from the previous chunk; none of the encodings we use
        // produce more chars than bytes otherwise.
        var chars = new char[count + 2];
        int charCount = decoder.GetChars(bytes, 0, count, chars, 0, flush);
        splitter.Append(chars.AsSpan(0, charCount));
    }

    /// <summary>
    /// Splits on both CR and LF. DISM and SFC redraw their progress with a bare CR, so a
    /// newline-only reader would hold back every progress update until the step finished.
    /// </summary>
    private sealed class LineSplitter(Action<string> onLine)
    {
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _all = new();

        public string AllText => _all.ToString();

        public void Append(ReadOnlySpan<char> chars)
        {
            foreach (char c in chars)
            {
                switch (c)
                {
                    case '\r' or '\n':
                        Emit();
                        break;
                    case '\0':
                        break;
                    default:
                        _line.Append(c);
                        break;
                }
            }
        }

        public void Complete() => Emit();

        private void Emit()
        {
            string text = _line.ToString().Trim();
            _line.Clear();
            if (text.Length == 0)
            {
                return;
            }

            _all.AppendLine(text);
            onLine(text);
        }
    }
}
