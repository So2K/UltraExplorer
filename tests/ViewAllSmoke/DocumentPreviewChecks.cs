using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media.Imaging;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task DocumentPreviewChecks()
    {
        Section("universal document and PDF content preview");
        var directory = Path.Combine(Path.GetTempPath(), "UltraExplorerDocumentPreview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string text = "Привет, мир!\r\nconst value = '你好 🌍';\r\n";
            var utf8 = Path.Combine(directory, "notes.txt");
            await File.WriteAllTextAsync(utf8, text, new UTF8Encoding(false));
            var content = await DocumentPreviewService.ReadAsync(utf8, 4096);
            Check("UTF-8 text preserves Unicode, emoji and line endings and is editable only when complete",
                content is { CanEdit: true, HasBom: false, EncodingName: "utf-8", IsBinary: false, Truncated: false }
                && content.Text == text);

            var utf16 = Path.Combine(directory, "wide.txt");
            await File.WriteAllTextAsync(utf16, text, Encoding.Unicode);
            var wide = await DocumentPreviewService.ReadAsync(utf16, 4096);
            Check("UTF-16 BOM is reported for encoding-preserving edits", wide is { CanEdit: true, HasBom: true,
                EncodingName: "utf-16" } && wide.Text == text);
            var utf16NoBom = Path.Combine(directory, "wide-no-bom");
            await File.WriteAllBytesAsync(utf16NoBom, Encoding.Unicode.GetBytes("Текст без BOM\nSecond line"));
            var noBom = await DocumentPreviewService.ReadAsync(utf16NoBom, 4096);
            Check("extensionless UTF-16 without a BOM is detected without showing raw NUL bytes",
                noBom is { CanEdit: true, HasBom: false, EncodingName: "utf-16" }
                && noBom.Text == "Текст без BOM\nSecond line");

            var utf32 = Path.Combine(directory, "wide32.txt");
            await File.WriteAllTextAsync(utf32, text, new UTF32Encoding(true, true));
            var wide32 = await DocumentPreviewService.ReadAsync(utf32, 4096);
            Check("UTF-32 big endian BOM is distinguished from UTF-16 and decoded exactly",
                wide32 is { CanEdit: true, HasBom: true, EncodingName: "utf-32BE" } && wide32.Text == text);

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            const string russian = "Русский текст из старого файла\r\nВторая строка";
            var legacy = Path.Combine(directory, "legacy.txt");
            await File.WriteAllBytesAsync(legacy, Encoding.GetEncoding(1251).GetBytes(russian));
            var old = await DocumentPreviewService.ReadAsync(legacy, 4096);
            Check("legacy Windows-1251 Cyrillic text exposes its original encoding for saving",
                old is { CanEdit: true, HasBom: false, EncodingName: "windows-1251" } && old.Text == russian);

            var unknownText = Path.Combine(directory, "custom.unknownformat");
            await File.WriteAllTextAsync(unknownText, text);
            Check("unknown file types are sniffed for real text content",
                (await DocumentPreviewService.ReadAsync(unknownText, 4096)) is { CanEdit: true } unknown
                && unknown.Text == text);
            var empty = Path.Combine(directory, "empty.txt");
            await File.WriteAllTextAsync(empty, "");
            Check("empty text files remain editable", (await DocumentPreviewService.ReadAsync(empty, 4096))
                is { CanEdit: true, Text.Length: 0 });

            var large = Path.Combine(directory, "partial.txt");
            await File.WriteAllTextAsync(large, new string('x', 25_000) + "🌍");
            var partial = await DocumentPreviewService.ReadAsync(large, 99);
            Check("large text is bounded and cannot be edited from a partial preview",
                partial is { CanEdit: false, Truncated: true, IsBinary: false } && partial.Text.Length <= 99);
            var surrogate = Path.Combine(directory, "emoji.txt");
            await File.WriteAllTextAsync(surrogate, "abcd🌍tail");
            var emoji = await DocumentPreviewService.ReadAsync(surrogate, 5);
            Check("preview truncation never ends on half of a Unicode surrogate pair",
                emoji is { CanEdit: false, Truncated: true } && emoji.Text == "abcd");

            var raw = Path.Combine(directory, "unknown.meshblob");
            await File.WriteAllBytesAsync(raw, [0, 1, 2, 3, 0xff, 0xaa, 0, 0xee, 0x50, 0x44, 0x46]);
            var binary = await DocumentPreviewService.ReadAsync(raw, 4096);
            Check("unrecognized binary files get actual hex bytes and metadata rather than an empty card",
                binary is { IsBinary: true, CanEdit: false } && binary.Text.Contains("00 01 02 03", StringComparison.Ordinal)
                && binary.Text.Contains("unknown.meshblob", StringComparison.Ordinal));
            var disguised = Path.Combine(directory, "executable.txt");
            await File.WriteAllBytesAsync(disguised, "MZ this is an executable header"u8.ToArray());
            Check("binary file signatures prevent text editing even under a text extension",
                (await DocumentPreviewService.ReadAsync(disguised, 4096)) is { IsBinary: true, CanEdit: false });

            await DocumentPackageChecks(directory);
            await DocumentLiteralPathChecks(directory);
            await DocumentPdfChecks(directory);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var textCanceled = false;
            var pdfCanceled = false;
            try { await DocumentPreviewService.ReadAsync(utf8, 4096, canceled.Token); }
            catch (OperationCanceledException) { textCanceled = true; }
            try { await PdfPreviewService.RenderAsync(Path.Combine(directory, "two-pages.pdf"), 0, 300, canceled.Token); }
            catch (OperationCanceledException) { pdfCanceled = true; }
            Check("already canceled document and PDF requests do not open files", textCanceled && pdfCanceled);
            Check("missing document files finish without a preview", await DocumentPreviewService.ReadAsync(
                Path.Combine(directory, "missing.txt"), 4096) is null);
        }
        finally { TryDelete(directory); }
    }

    private static async Task DocumentPackageChecks(string directory)
    {
        var word = Path.Combine(directory, "document.docx");
        WriteDocumentPackage(word, new Dictionary<string, string>
        {
            ["word/document.xml"] = "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'>"
                + "<w:body><w:p><w:r><w:t>Первый абзац</w:t></w:r><w:r><w:t> with runs</w:t></w:r></w:p>"
                + "<w:p><w:r><w:t>Second paragraph</w:t></w:r></w:p></w:body></w:document>",
            ["word/vbaProject.bin"] = "Macros must not be executed"
        });
        var document = await DocumentPreviewService.ReadAsync(word, 4096);
        Check("DOCX displays real paragraphs and joined text runs while preserving the package as read-only",
            document is { CanEdit: false, IsBinary: false } && document.Text.Contains("Первый абзац with runs\nSecond paragraph", StringComparison.Ordinal)
            && !document.Text.Contains("Macros must", StringComparison.Ordinal));

        var slides = Path.Combine(directory, "deck.pptx");
        WriteDocumentPackage(slides, new Dictionary<string, string>
        {
            ["ppt/slides/slide10.xml"] = "<a:p xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:r><a:t>Tenth slide</a:t></a:r></a:p>",
            ["ppt/slides/slide2.xml"] = "<a:p xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:r><a:t>Second slide</a:t></a:r></a:p>",
            ["ppt/slides/_rels/slide2.xml.rels"] = "<bad/>"
        });
        var deck = await DocumentPreviewService.ReadAsync(slides, 4096);
        Check("PowerPoint slides are read as text in numeric order, excluding relationship files",
            deck is { CanEdit: false, IsBinary: false } && deck.Text.IndexOf("Second slide", StringComparison.Ordinal)
            < deck.Text.IndexOf("Tenth slide", StringComparison.Ordinal));

        var workbook = Path.Combine(directory, "book.xlsx");
        WriteDocumentPackage(workbook, new Dictionary<string, string>
        {
            ["xl/sharedStrings.xml"] = "<sst xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'>"
                + "<si><t>Привет</t></si><si><r><t>Rich</t></r><r><t> text</t></r></si></sst>",
            ["xl/worksheets/sheet1.xml"] = "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData>"
                + "<row><c r='A1' t='s'><v>0</v></c><c r='B1' t='s'><v>1</v></c><c r='C1' t='b'><v>1</v></c></row>"
                + "<row><c r='A2' t='inlineStr'><is><t>Inline</t></is></c><c r='B2'><f>1+1</f><v>2</v></c></row>"
                + "</sheetData></worksheet>"
        });
        var cells = await DocumentPreviewService.ReadAsync(workbook, 4096);
        Check("Excel displays shared strings, rich strings, inline cells, booleans and cached formula values",
            cells is { CanEdit: false, IsBinary: false } && cells.Text.Contains("A1: Привет", StringComparison.Ordinal)
            && cells.Text.Contains("B1: Rich text", StringComparison.Ordinal) && cells.Text.Contains("C1: TRUE", StringComparison.Ordinal)
            && cells.Text.Contains("A2: Inline", StringComparison.Ordinal) && cells.Text.Contains("B2: 2", StringComparison.Ordinal));

        var open = Path.Combine(directory, "document.odt");
        WriteDocumentPackage(open, new Dictionary<string, string>
        {
            ["content.xml"] = "<office:document-content xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0' "
                + "xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0'><office:scripts><script>HIDDEN_SCRIPT_MARKER</script></office:scripts>"
                + "<office:body><text:p>ODT текст</text:p>"
                + "<text:p>Another paragraph</text:p></office:body></office:document-content>"
        });
        var odt = await DocumentPreviewService.ReadAsync(open, 4096);
        Check("OpenDocument paragraphs have a real content preview", odt is { CanEdit: false, IsBinary: false }
            && odt.Text.Contains("ODT текст\nAnother paragraph", StringComparison.Ordinal)
            && !odt.Text.Contains("HIDDEN_SCRIPT_MARKER", StringComparison.Ordinal));

        var rtf = Path.Combine(directory, "formatted.rtf");
        await File.WriteAllTextAsync(rtf, "{\\rtf1\\ansi\\ansicpg1251{\\fonttbl{\\f0 SecretFont;}}"
            + "Visible \\u1055?\\u1088?\\u1080?\\u1074?\\u1077?\\u1090?\\par "
            + "\\'ec\\'e8\\'f0 {\\*\\hidden SecretDestination} end}", Encoding.ASCII);
        var rich = await DocumentPreviewService.ReadAsync(rtf, 4096);
        Check("RTF extracts Unicode and legacy text while excluding font tables and ignored destinations",
            rich is { CanEdit: false, IsBinary: false } && rich.Text.Contains("Visible Привет\nмир", StringComparison.Ordinal)
            && !rich.Text.Contains("Secret", StringComparison.Ordinal));

        var zip = Path.Combine(directory, "files.zip");
        WriteDocumentPackage(zip, new Dictionary<string, string>
        {
            ["folder/notes.txt"] = "archive item", ["../../traversal.txt"] = "must never extract"
        });
        var listing = await DocumentPreviewService.ReadAsync(zip, 4096);
        Check("ZIP shows entry names and sizes without extracting even traversal-shaped entries",
            listing is { CanEdit: false, IsBinary: false } && listing.Text.Contains("folder/notes.txt", StringComparison.Ordinal)
            && listing.Text.Contains("../../traversal.txt", StringComparison.Ordinal)
            && !File.Exists(Path.Combine(directory, "traversal.txt")));

        var external = Path.Combine(directory, "private-marker.txt");
        await File.WriteAllTextAsync(external, "PRIVATE_ENTITY_MARKER");
        var unsafeXml = Path.Combine(directory, "entity.docx");
        WriteDocumentPackage(unsafeXml, new Dictionary<string, string>
        {
            ["word/document.xml"] = $"<!DOCTYPE root [<!ENTITY secret SYSTEM '{new Uri(external).AbsoluteUri}'>]>"
                + "<root><t>&secret;</t></root>"
        });
        var blocked = await DocumentPreviewService.ReadAsync(unsafeXml, 4096);
        Check("document XML never resolves local files or external entities",
            blocked is { CanEdit: false, IsBinary: true } && !blocked.Text.Contains("PRIVATE_ENTITY_MARKER", StringComparison.Ordinal));

        var oversized = Path.Combine(directory, "oversized.docx");
        WriteDocumentPackage(oversized, new Dictionary<string, string>
        {
            ["word/document.xml"] = new string('x', 8 * 1024 * 1024 + 1)
        });
        Check("highly compressed document parts are rejected before decompression exceeds the XML budget",
            (await DocumentPreviewService.ReadAsync(oversized, 4096)) is { CanEdit: false, IsBinary: true });

        var forged = Path.Combine(directory, "forged-directory.zip");
        using (var output = File.Create(forged))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            for (var index = 0; index < 20_001; index++) archive.CreateEntry($"{index}.txt");
        var forgedBytes = await File.ReadAllBytesAsync(forged);
        // Forge both EOCD entry counts: preview must count actual central
        // records before the archive library allocates their metadata objects.
        Array.Clear(forgedBytes, forgedBytes.Length - 22 + 8, 4);
        await File.WriteAllBytesAsync(forged, forgedBytes);
        Check("forged ZIP entry counts cannot bypass the bounded directory scan",
            (await DocumentPreviewService.ReadAsync(forged, 4096)) is { CanEdit: false, IsBinary: true });
    }

    private static async Task DocumentLiteralPathChecks(string directory)
    {
        var normal = Path.Combine(directory, "literal.txt");
        var literal = Path.Combine(directory, "literal.txt ");
        await File.WriteAllTextAsync(normal, "normal neighbour");
        try
        {
            await File.WriteAllTextAsync(DocumentPreviewService.LiteralPath(literal), "literal trailing space");
            var preview = await DocumentPreviewService.ReadAsync(literal, 4096);
            Check("document preview preserves literal Windows file names rather than reading a neighbour",
                preview?.Text == "literal trailing space");
        }
        finally { File.Delete(DocumentPreviewService.LiteralPath(literal)); }
    }

    private static async Task DocumentPdfChecks(string directory)
    {
        var path = Path.Combine(directory, "two-pages.pdf");
        WriteDocumentPdfFixture(path);
        var pages = await Task.WhenAll(PdfPreviewService.RenderAsync(path, 0, 300), PdfPreviewService.RenderAsync(path, 1, 300));
        Check("real PDF pages rasterize with the correct page count, aspect ratios and frozen bounded images",
            pages.All(page => page.PageCount == 2 && page.Image.IsFrozen)
            && pages[0].Image.PixelWidth == 300 && pages[0].Image.PixelHeight == 150
            && pages[1].Image.PixelWidth == 150 && pages[1].Image.PixelHeight == 300);
        var first = DocumentPixel(pages[0].Image);
        var second = DocumentPixel(pages[1].Image);
        Check("PDF page navigation renders different real content instead of reusing its first thumbnail",
            first[2] > 240 && first[0] < 10 && second[0] > 240 && second[2] < 10);
        var clamped = await PdfPreviewService.RenderAsync(path, 999, 128);
        Check("out-of-range PDF pages and requested image size are safely bounded",
            clamped.PageCount == 2 && clamped.Image.PixelHeight == 128 && DocumentPixel(clamped.Image)[0] > 240);
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check("PDF requests release source-file handles after rendering", exclusive.Length > 0);

        var bad = Path.Combine(directory, "broken.pdf");
        await File.WriteAllTextAsync(bad, "not a PDF");
        var rejected = false;
        try { await PdfPreviewService.RenderAsync(bad, 0, 300); }
        catch (InvalidDataException) { rejected = true; }
        var again = await PdfPreviewService.RenderAsync(path, 0, 64);
        Check("a malformed PDF fails without poisoning subsequent real rendering", rejected && again.PageCount == 2);
    }

    private static byte[] DocumentPixel(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        var offset = (image.PixelHeight / 2 * image.PixelWidth + image.PixelWidth / 2) * 4;
        return pixels[offset..(offset + 4)];
    }

    private static void WriteDocumentPackage(string path, IReadOnlyDictionary<string, string> parts)
    {
        using var output = File.Create(path);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var part in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), new UTF8Encoding(false));
            writer.Write(part.Value);
        }
    }

    private static void WriteDocumentPdfFixture(string path)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>",
            PdfContent("1 0 0 rg 0 0 200 100 re f\n"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents 6 0 R >>",
            PdfContent("0 0 1 rg 0 0 100 200 re f\n")
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var positions = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            positions.Add(builder.Length);
            builder.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = builder.Length;
        builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in positions.Skip(1)) builder.Append($"{offset:D10} 00000 n \n");
        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, builder.ToString(), Encoding.ASCII);
        static string PdfContent(string content) => $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream";
    }
}
