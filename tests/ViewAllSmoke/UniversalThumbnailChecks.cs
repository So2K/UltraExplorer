using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task UniversalThumbnailChecks()
    {
        Section("universal hover: real text, documents, PDF and binary contents");
        var directory = Path.Combine(Path.GetTempPath(), "UltraExplorerUniversalThumbnail", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var service = new FileThumbnailService();
            var textPath = Path.Combine(directory, "utf8.txt");
            const string content = "Привет, 世界 — actual contents\nSecond line 42";
            await File.WriteAllTextAsync(textPath, content, new UTF8Encoding(false));
            var text = await DocumentPreviewService.ReadAsync(textPath, 4_096);
            var preview = await ThumbnailAwait(service.GetAsync(textPath));
            Check("UTF-8 with Cyrillic and CJK is decoded as the actual document text",
                text?.Text == content && !text.IsBinary && text.CanEdit);
            Check("plain text produces frozen bounded content pixels without a Shell handler",
                UniversalThumbnailPixels(preview) && preview!.Detail == text?.Detail
                && preview.OriginalWidth is null && preview.OriginalHeight is null);

            var utf16Path = Path.Combine(directory, "unicode.txt");
            await File.WriteAllTextAsync(utf16Path, content, new UnicodeEncoding(false, true));
            var utf16 = await DocumentPreviewService.ReadAsync(utf16Path, 4_096);
            var utf16Preview = await ThumbnailAwait(service.GetAsync(utf16Path));
            Check("UTF-16 BOM contents remain readable in the universal hover provider",
                utf16?.Text == content && !utf16.IsBinary && UniversalThumbnailPixels(utf16Preview)
                && UniversalPixelHash(preview!.Image) == UniversalPixelHash(utf16Preview!.Image));

            var aPath = Path.Combine(directory, "one.custom-format");
            var bPath = Path.Combine(directory, "two.custom-format");
            await File.WriteAllTextAsync(aPath, "AAAAAAAAA actual file one");
            await File.WriteAllTextAsync(bPath, "BBBBBBBBB actual file two");
            var a = await ThumbnailAwait(service.GetAsync(aPath));
            var b = await ThumbnailAwait(service.GetAsync(bPath));
            Check("unregistered text formats show different actual contents rather than one type icon",
                UniversalThumbnailPixels(a) && UniversalThumbnailPixels(b)
                && UniversalPixelHash(a!.Image) != UniversalPixelHash(b!.Image));

            var emptyPath = Path.Combine(directory, "LICENSE");
            await File.WriteAllTextAsync(emptyPath, "");
            Check("extensionless and empty files have a readable content preview",
                UniversalThumbnailPixels(await ThumbnailAwait(service.GetAsync(emptyPath))));

            var binaryPath = Path.Combine(directory, "unknown.ultra-binary");
            var bytes = Enumerable.Range(0, 2_048).Select(index => (byte)index).ToArray();
            await File.WriteAllBytesAsync(binaryPath, bytes);
            var binary = await DocumentPreviewService.ReadAsync(binaryPath, 4_096);
            var binaryPreview = await ThumbnailAwait(service.GetAsync(binaryPath));
            Check("unknown binary files expose bounded actual hexadecimal data",
                binary is { IsBinary: true, CanEdit: false } && binary.Text.Contains("00 01 02 03")
                && UniversalThumbnailPixels(binaryPreview) && binaryPreview!.Detail == binary.Detail);

            var docxPath = Path.Combine(directory, "note.docx");
            using (var file = File.Create(docxPath))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("word/document.xml");
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                await writer.WriteAsync("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Actual DOCX paragraph 314</w:t></w:r></w:p></w:body></w:document>""");
            }
            var docx = await DocumentPreviewService.ReadAsync(docxPath, 4_096);
            var documentPreview = await ThumbnailAwait(service.GetAsync(docxPath));
            Check("DOCX hover renders extracted paragraph contents without Word or Office",
                docx?.Text.Contains("Actual DOCX paragraph 314") == true && !docx.CanEdit
                && UniversalThumbnailPixels(documentPreview) && documentPreview!.Detail == docx.Detail);

            var pdfPath = Path.Combine(directory, "two-pages.pdf");
            UniversalWritePdf(pdfPath);
            var pdf = await ThumbnailAwait(service.GetAsync(pdfPath));
            Check("PDF hover renders the actual first page with the real page count",
                pdf is { Detail: "PDF · Page 1 of 2" } && UniversalThumbnailPixels(pdf)
                && UniversalHasRed(pdf.Image));

            var pngPath = Path.Combine(directory, "existing-image.png");
            var image = new PngBitmapEncoder();
            image.Frames.Add(BitmapFrame.Create(ThumbnailPicture(1_024, 512, 81)));
            using (var file = File.Create(pngPath)) image.Save(file);
            var png = await ThumbnailAwait(service.GetAsync(pngPath));
            Check("existing image decoding retains original dimensions, pixels and its 512-pixel bound",
                png is { OriginalWidth: 1_024, OriginalHeight: 512, Detail: null } && png.Image.IsFrozen
                && png.Image.PixelWidth == 512 && png.Image.PixelHeight == 256 && ThumbnailBlue(png.Image) == 81);

            File.Delete(textPath);
            File.Delete(pdfPath);
            File.Delete(docxPath);
            Check("document and PDF preview results detach from files and invalidation removes missing entries",
                await ThumbnailAwait(service.GetAsync(textPath)) is null
                && await ThumbnailAwait(service.GetAsync(pdfPath)) is null
                && await ThumbnailAwait(service.GetAsync(docxPath)) is null);
            await UniversalThumbnailCancellationChecks(directory);
        }
        finally { TryDelete(directory); }
    }

    private static bool UniversalThumbnailPixels(ThumbnailResult? result)
    {
        if (result is null || !result.Image.IsFrozen || result.Image.PixelWidth is <= 0 or > 512
            || result.Image.PixelHeight is <= 0 or > 512) return false;
        var bitmap = new FormatConvertedBitmap(result.Image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        // Actual content must have multiple visible pixel values. A blank
        // rectangle or transparent bitmap does not meet the content contract.
        return Enumerable.Range(0, pixels.Length / 4).Where(index => pixels[index * 4 + 3] > 0)
            .Select(index => (pixels[index * 4], pixels[index * 4 + 1], pixels[index * 4 + 2])).Distinct().Take(2).Count() >= 2;
    }

    private static async Task UniversalThumbnailCancellationChecks(string directory)
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var firstCancel = new CancellationTokenSource();
        using var secondCancel = new CancellationTokenSource();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1,
            MetadataReader = _ => ThumbnailStamp(),
            CancellableThumbnailReader = (_, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.TrySetResult(token);
                    token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                    token.ThrowIfCancellationRequested();
                }
                return new ThumbnailResult(ThumbnailPicture());
            }
        });
        var path = Path.Combine(directory, "cancel-owned-renderer.pdf");
        var first = service.GetAsync(path, firstCancel.Token);
        var second = service.GetAsync(path, secondCancel.Token);
        var contentToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        firstCancel.Cancel();
        try { await ThumbnailAwait(first); } catch (OperationCanceledException) { }
        Check("canceling one shared hover keeps its owned renderer alive for the other waiter",
            !contentToken.IsCancellationRequested);
        secondCancel.Cancel();
        try { await ThumbnailAwait(second); } catch (OperationCanceledException) { }
        var fresh = await ThumbnailAwait(service.GetAsync(path));
        Check("the last canceled hover stops its owned content work and a fresh hover obtains a free worker",
            contentToken.IsCancellationRequested && fresh is not null && calls == 2 && service.Diagnostics.WorkerCount == 1);
    }

    private static ulong UniversalPixelHash(BitmapSource image)
    {
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var hash = 1_469_598_103_934_665_603UL;
        foreach (var value in pixels) hash = unchecked((hash ^ value) * 1_099_511_628_211UL);
        return hash;
    }

    private static bool UniversalHasRed(BitmapSource image)
    {
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return Enumerable.Range(0, pixels.Length / 4).Count(index => pixels[index * 4 + 2] > 190
            && pixels[index * 4] < 70 && pixels[index * 4 + 1] < 70 && pixels[index * 4 + 3] > 200) > 1_000;
    }

    private static void UniversalWritePdf(string path)
    {
        var first = "1 0 0 rg 30 30 100 100 re f\n";
        var second = "0 0 1 rg 30 30 100 100 re f\n";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            $"<< /Length {first.Length} >>\nstream\n{first}endstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R >>",
            $"<< /Length {second.Length} >>\nstream\n{second}endstream"
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var positions = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            positions.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var position in positions) pdf.Append(position.ToString("D10")).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(pdf.ToString()));
    }
}
