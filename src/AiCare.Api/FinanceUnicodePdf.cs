using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace AiCare.Api;

/// <summary>Embedded, licensed fonts only. Unsupported glyphs fail before any document is archived.</summary>
internal static class FinanceUnicodePdf
{
    internal const float PageWidth = 595, PageHeight = 842, Margin = 36, FontSize = 10, LineHeight = 17;
    internal const int LinesPerPage = 42;
    private static readonly Lazy<SKTypeface[]> Faces = new(() => new[] { Load("NotoSans.ttf"), Load("NotoSansArabic.ttf"), Load("NotoSansSC.otf") });
    private static SKTypeface Load(string name)
    {
        using var stream = typeof(FinanceUnicodePdf).Assembly.GetManifestResourceStream($"AiCare.Api.Fonts.{name}")
            ?? throw new InvalidOperationException($"Embedded finance PDF font {name} is missing.");
        using var font = new MemoryStream();
        stream.CopyTo(font);
        var bytes = font.GetBuffer().AsSpan(0, checked((int)font.Length));
        if (bytes.Length < 12) throw new InvalidOperationException($"Embedded finance PDF font {name} is invalid.");
        var tables = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        if (12 + tables * 16 > bytes.Length) throw new InvalidOperationException($"Embedded finance PDF font {name} has an invalid table directory.");
        for (var i = 0; i < tables; i++)
        {
            // Skia exports variable fonts as Type 3 outlines rather than an embedded font program.
            // Fail clearly if a future asset update accidentally reintroduces a variable font.
            if (bytes.Slice(12 + i * 16, 4).SequenceEqual("fvar"u8))
                throw new InvalidOperationException($"Finance PDF font {name} must be a static font for PDF embedding.");
        }
        font.Position = 0;
        return SKTypeface.FromStream(font) ?? throw new InvalidOperationException($"Embedded finance PDF font {name} is invalid.");
    }

    private sealed class TextContext : IDisposable
    {
        private readonly SKFont[] fonts = Faces.Value.Select(face => new SKFont(face, FontSize)).ToArray();
        private readonly SKShaper[] shapers = Faces.Value.Select(face => new SKShaper(face)).ToArray();
        private sealed record Run(string Text, int Font, bool Rtl);

        private List<Run> Runs(string text)
        {
            ValidateUtf16(text);
            var result = new List<Run>();
            var elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                if (element.EnumerateRunes().Any(r => Rune.IsControl(r) || (Rune.GetUnicodeCategory(r) == UnicodeCategory.Format && r.Value is not (0x200C or 0x200D))))
                    throw new FinancePdfTextException();
                var rtl = element.EnumerateRunes().Any(r => r.Value is >= 0x0590 and <= 0x08FF or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF);
                var neutral = element.EnumerateRunes().All(r => Rune.IsWhiteSpace(r) || Rune.IsPunctuation(r));
                var preferred = rtl ? 1 : 0;
                if (neutral && result.Count > 0) preferred = result[^1].Font;
                var font = fonts[preferred].ContainsGlyphs(element) ? preferred : Array.FindIndex(fonts, f => f.ContainsGlyphs(element));
                if (font < 0) throw new FinancePdfTextException();
                if (neutral && result.Count > 0) rtl = result[^1].Rtl;
                if (result.Count > 0 && result[^1].Font == font && result[^1].Rtl == rtl)
                    result[^1] = result[^1] with { Text = result[^1].Text + element };
                else result.Add(new Run(element, font, rtl));
            }
            return result;
        }

        public float Width(string text) => Runs(text).Sum(run => Shape(run).Width);
        private SKShaper.Result Shape(Run run)
        {
            var shape = shapers[run.Font].Shape(run.Text, fonts[run.Font]);
            if (shape.Codepoints.Any(g => g == 0)) throw new FinancePdfTextException();
            return shape;
        }

        public void Draw(SKCanvas canvas, string text, float y, SKPaint paint)
        {
            var runs = Runs(text);
            // Script runs keep Latin financial identifiers in their logical order while Arabic runs are shaped RTL.
            var rtlBase = runs.FirstOrDefault(r => r.Text.EnumerateRunes().Any(Rune.IsLetter))?.Rtl == true;
            if (rtlBase) runs.Reverse();
            var width = runs.Sum(r => Shape(r).Width);
            var x = rtlBase ? PageWidth - Margin - width : Margin;
            foreach (var run in runs)
            {
                var shaped = Shape(run);
                canvas.DrawShapedText(shapers[run.Font], run.Text, new SKPoint(x, y), SKTextAlign.Left, fonts[run.Font], paint);
                x += shaped.Width;
            }
        }

        public void Dispose() { foreach (var s in shapers) s.Dispose(); foreach (var f in fonts) f.Dispose(); }
    }

    private static void ValidateUtf16(string text)
    {
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done) throw new FinancePdfTextException();
            remaining = remaining[consumed..];
        }
    }

    internal static IReadOnlyList<string> Layout(IEnumerable<string> text)
    {
        using var context = new TextContext();
        return Layout(text, context);
    }

    private static List<string> Layout(IEnumerable<string> text, TextContext context)
    {
        var result = new List<string>();
        foreach (var value in text)
        {
            ValidateUtf16(value);
            foreach (var logical in value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n'))
            {
                var line = new StringBuilder(); var elements = StringInfo.GetTextElementEnumerator(logical);
                while (elements.MoveNext())
                {
                    var element = elements.GetTextElement(); var candidate = line.ToString() + element;
                    if (context.Width(candidate) > PageWidth - 2 * Margin)
                    {
                        if (line.Length == 0) throw new FinancePdfTextException();
                        result.Add(line.ToString()); line.Clear();
                        if (context.Width(element) > PageWidth - 2 * Margin) throw new FinancePdfTextException();
                    }
                    line.Append(element);
                }
                result.Add(line.ToString());
            }
        }
        return result;
    }

    internal static bool Supports(IEnumerable<string> text)
    {
        try { using var context = new TextContext(); foreach (var value in text) Layout([value], context); return true; }
        catch (FinancePdfTextException) { return false; }
    }

    internal static byte[] Create(string title, IReadOnlyList<string> headers, IReadOnlyList<object?[]> rows)
    {
        using var context = new TextContext();
        var titleLines = Layout([title], context);
        var values = new List<string>();
        foreach (var row in rows)
        {
            if (row.Length != headers.Count) throw new ArgumentException("Each export row must match the header count.");
            values.Add("");
            for (var i = 0; i < headers.Count; i++) values.Add(headers[i] + ": " + (Convert.ToString(row[i], CultureInfo.InvariantCulture) ?? ""));
        }
        // Long titles are body content, with a short page header; neither titles nor final records are clipped.
        var lines = Layout(values, context);
        lines.InsertRange(0, titleLines);
        if (lines.Count == 0) lines.Add("");
        var pages = lines.Chunk(LinesPerPage).ToArray();
        using var output = new MemoryStream();
        using (var document = SKDocument.CreatePdf(output, new SKDocumentPdfMetadata { Title = title, Creator = "AiCare finance" }))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            for (var page = 0; page < pages.Length; page++)
            {
                var canvas = document.BeginPage(PageWidth, PageHeight);
                context.Draw(canvas, "AiCare finance", Margin, paint);
                for (var i = 0; i < pages[page].Length; i++) context.Draw(canvas, pages[page][i], Margin + 32 + i * LineHeight, paint);
                context.Draw(canvas, $"Page {page + 1} / {pages.Length}", PageHeight - Margin, paint);
                document.EndPage();
            }
            document.Close();
        }
        return output.ToArray();
    }
}
