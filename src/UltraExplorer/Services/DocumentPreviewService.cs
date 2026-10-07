using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace UltraExplorer.Services;

internal sealed record DocumentPreviewData(string Text, string Detail, bool CanEdit,
    string? EncodingName = null, bool HasBom = false, bool Truncated = false, bool IsBinary = false);

/// <summary>
/// Read-only, bounded content extraction. Office/OpenDocument packages are read
/// as data: no Office process, scripts, formulas, macros or external XML entities
/// are executed. Unsupported formats still get a useful metadata/hex preview.
/// </summary>
internal static class DocumentPreviewService
{
    private const int MaximumCharacters = 2 * 1024 * 1024;
    private const int MaximumXmlBytes = 8 * 1024 * 1024;
    private const int MaximumArchiveEntries = 20_000;
    private const long MaximumArchiveBytes = 512L * 1024 * 1024;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".rst", ".log", ".csv", ".tsv", ".json", ".jsonl", ".xml",
        ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf", ".config", ".properties", ".env", ".sql",
        ".cs", ".csx", ".vb", ".fs", ".fsx", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".py",
        ".pyw", ".rb", ".php", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".sh", ".bash", ".zsh",
        ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hxx", ".rs", ".go", ".java", ".kt", ".kts",
        ".swift", ".m", ".mm", ".lua", ".r", ".dart", ".vue", ".svelte", ".html", ".htm", ".css",
        ".scss", ".sass", ".less", ".svg", ".tex", ".bib", ".srt", ".vtt", ".ass", ".lrc", ".obj",
        ".mtl", ".gcode", ".gitignore", ".gitattributes", ".editorconfig", ".sln", ".slnx", ".csproj",
        ".vbproj", ".fsproj", ".props", ".targets", ".resx", ".xaml", ".axaml", ".desktop", ".url"
    };
    private static readonly HashSet<string> PackageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx", ".dotm", ".xlsx", ".xlsm", ".xltx", ".xltm", ".pptx", ".pptm",
        ".ppsx", ".ppsm", ".potx", ".potm", ".odt", ".ods", ".odp", ".ott", ".ots", ".otp", ".epub"
    };

    static DocumentPreviewService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Task<DocumentPreviewData?> ReadAsync(string path, int maxChars, CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<DocumentPreviewData?>(token);
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<DocumentPreviewData?>(null);
        return Task.Run(() => Read(path, Math.Clamp(maxChars, 1, MaximumCharacters), token), token);
    }

    private static DocumentPreviewData? Read(string path, int maxChars, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var literal = LiteralPath(path);
            using var stream = new FileStream(literal, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var extension = Path.GetExtension(path.TrimEnd(' ', '.')).ToLowerInvariant();
            if (PackageExtensions.Contains(extension) || extension is ".zip" or ".jar" or ".nupkg" or ".vsix" or ".cbz")
            {
                try { return ReadPackage(stream, path, extension, maxChars, token); }
                catch (Exception ex) when (ex is InvalidDataException or XmlException or NotSupportedException)
                {
                    stream.Position = 0;
                    return BinaryPreview(stream, path, maxChars, token,
                        $"{extension.TrimStart('.').ToUpperInvariant()} · Content could not be extracted");
                }
            }

            var byteBudget = Math.Min(MaximumCharacters * 4 + 4, checked(maxChars * 4 + 4));
            var bytes = ReadPrefix(stream, byteBudget, token);
            var byteTruncated = stream.Length > bytes.Length;
            if (extension == ".rtf" && bytes.AsSpan().StartsWith("{\\rtf"u8))
            {
                var rtf = ExtractRtf(bytes, maxChars, token, out var truncated);
                return new(rtf, "RTF · Text content", false, Truncated: truncated || byteTruncated);
            }

            var encoding = DetectEncoding(bytes, out var bomLength);
            var knownText = TextExtensions.Contains(extension)
                || Path.GetFileName(path) is "LICENSE" or "README" or "Makefile" or "Dockerfile";
            if (encoding is null)
            {
                if (LooksBinary(bytes) || !knownText)
                {
                    // Unknown extensions can contain ordinary UTF-8 text. The
                    // strict decoder distinguishes that from legacy/raw bytes.
                    try
                    {
                        encoding = new UTF8Encoding(false, true);
                        _ = DecodePrefix(bytes, 0, encoding, byteTruncated);
                        if (LooksBinary(bytes)) encoding = null;
                    }
                    catch (DecoderFallbackException) { encoding = null; }
                    if (encoding is null)
                    {
                        stream.Position = 0;
                        return BinaryPreview(stream, path, maxChars, token);
                    }
                }
                else
                {
                    encoding = new UTF8Encoding(false, true);
                    try { _ = DecodePrefix(bytes, 0, encoding, byteTruncated); }
                    catch (DecoderFallbackException) { encoding = Encoding.GetEncoding(1251); }
                }
            }

            string text;
            try { text = DecodePrefix(bytes, bomLength, encoding, byteTruncated); }
            catch (DecoderFallbackException)
            {
                stream.Position = 0;
                return BinaryPreview(stream, path, maxChars, token, "Invalid text encoding · Raw data");
            }
            if (text.Any(c => c == '\0' || c < ' ' && c is not '\r' and not '\n' and not '\t' and not '\f'))
            {
                stream.Position = 0;
                return BinaryPreview(stream, path, maxChars, token);
            }
            var isTruncated = byteTruncated || text.Length > maxChars;
            if (text.Length > maxChars) text = SafeCut(text, maxChars);
            var detail = $"{encoding.EncodingName} · {FormatBytes(stream.Length)}"
                + (isTruncated ? " · Partial preview" : "");
            return new(text, detail, !isTruncated, encoding.WebName, bomLength != 0, isTruncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        { return null; }
    }

    internal static string LiteralPath(string path)
    {
        path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        // Do not call GetFullPath on a fully qualified spelling: Windows would
        // normalize literal trailing spaces/dots into a different neighbour.
        if (!Path.IsPathFullyQualified(path)) path = Path.GetFullPath(path);
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    private static byte[] ReadPrefix(Stream stream, int count, CancellationToken token)
    {
        var bytes = new byte[(int)Math.Min(stream.Length, count)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(bytes.AsSpan(offset, Math.Min(32 * 1024, bytes.Length - offset)));
            if (read == 0) break;
            offset += read;
        }
        return offset == bytes.Length ? bytes : bytes[..offset];
    }

    private static Encoding? DetectEncoding(byte[] bytes, out int bomLength)
    {
        bomLength = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }))
        { bomLength = 4; return new UTF32Encoding(false, false, true); }
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
        { bomLength = 4; return new UTF32Encoding(true, false, true); }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        { bomLength = 3; return new UTF8Encoding(false, true); }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        { bomLength = 2; return new UnicodeEncoding(false, false, true); }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        { bomLength = 2; return new UnicodeEncoding(true, false, true); }

        var pairs = Math.Min(bytes.Length / 2, 2048);
        if (pairs < 4) return null;
        var evenZero = 0;
        var oddZero = 0;
        var evenLow = 0;
        var oddLow = 0;
        for (var index = 0; index < pairs * 2; index += 2)
        {
            if (bytes[index] == 0) evenZero++;
            if (bytes[index + 1] == 0) oddZero++;
            if (bytes[index] <= 0x10) evenLow++;
            if (bytes[index + 1] <= 0x10) oddLow++;
        }
        if (oddZero > pairs / 3 && evenZero < pairs / 10
            || oddLow > pairs * .8 && evenLow < pairs * .2) return new UnicodeEncoding(false, false, true);
        if (evenZero > pairs / 3 && oddZero < pairs / 10
            || evenLow > pairs * .8 && oddLow < pairs * .2) return new UnicodeEncoding(true, false, true);
        return null;
    }

    private static string DecodePrefix(byte[] bytes, int skip, Encoding encoding, bool partial)
    {
        var input = bytes.AsSpan(skip);
        var chars = new char[encoding.GetMaxCharCount(input.Length)];
        encoding.GetDecoder().Convert(input, chars, flush: !partial, out _, out var used, out _);
        return new string(chars, 0, used);
    }

    private static bool LooksBinary(byte[] bytes)
    {
        ReadOnlySpan<byte> sample = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        if (sample.StartsWith("MZ"u8) || sample.StartsWith("PK\x03\x04"u8) || sample.StartsWith("%PDF-"u8)
            || sample.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47 })
            || sample.StartsWith(new byte[] { 0xff, 0xd8, 0xff }) || sample.StartsWith("GIF8"u8)
            || sample.StartsWith(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c })
            || sample.StartsWith("Rar!"u8) || sample.StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0 })
            || sample.StartsWith(new byte[] { 0x1f, 0x8b }) || sample.StartsWith("SQLite format"u8)
            || sample.StartsWith("RIFF"u8)) return true;
        foreach (var value in sample)
            if (value == 0 || value < 32 && value is not 9 and not 10 and not 12 and not 13) return true;
        return false;
    }

    private static DocumentPreviewData BinaryPreview(Stream stream, string path, int maxChars,
        CancellationToken token, string? detail = null)
    {
        var bytes = ReadPrefix(stream, Math.Min(1024, Math.Max(16, maxChars / 5)), token);
        var text = new LimitedText(maxChars);
        var info = new FileInfo(LiteralPath(path));
        text.Line(Path.GetFileName(path));
        text.Line($"Size: {FormatBytes(stream.Length)} ({stream.Length:N0} bytes)");
        text.Line($"Modified: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        text.Line();
        text.Line("Raw data");
        for (var offset = 0; offset < bytes.Length && !text.Full; offset += 16)
        {
            token.ThrowIfCancellationRequested();
            var row = bytes.AsSpan(offset, Math.Min(16, bytes.Length - offset));
            var hex = string.Join(" ", row.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
            var ascii = new string(row.ToArray().Select(value => value is >= 32 and < 127 ? (char)value : '.').ToArray());
            text.Line($"{offset:X8}  {hex.PadRight(47)}  {ascii}");
        }
        return new(text.ToString(), detail ?? $"{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} · File information",
            false, Truncated: stream.Length > bytes.Length || text.Truncated, IsBinary: true);
    }

    private static DocumentPreviewData ReadPackage(FileStream stream, string path, string extension,
        int maxChars, CancellationToken token)
    {
        EnsureBoundedArchive(stream, token);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaximumArchiveEntries) throw new InvalidDataException("Too many archive entries.");
        var text = new LimitedText(maxChars);
        var detail = "Archive";
        var extracted = false;
        if (extension.StartsWith(".doc", StringComparison.Ordinal) || extension is ".dotx" or ".dotm")
        {
            detail = "Word · Text content";
            var entry = archive.GetEntry("word/document.xml");
            if (entry is not null) { AppendXmlText(entry, text, token); extracted = true; }
        }
        else if (extension.StartsWith(".ppt", StringComparison.Ordinal) || extension.StartsWith(".pps", StringComparison.Ordinal)
            || extension.StartsWith(".pot", StringComparison.Ordinal))
        {
            detail = "PowerPoint · Slide text";
            var slides = archive.Entries.Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                && entry.FullName.EndsWith(".xml", StringComparison.Ordinal)
                && entry.FullName["ppt/slides/slide".Length..^4].All(char.IsAsciiDigit))
                .OrderBy(entry => PartNumber(entry.FullName)).ToArray();
            if (slides.Length > 200) text.MarkTruncated();
            foreach (var slide in slides.Take(200))
            {
                token.ThrowIfCancellationRequested();
                text.Line($"Slide {PartNumber(slide.FullName)}");
                AppendXmlText(slide, text, token);
                text.Line();
                extracted = true;
                if (text.Full) break;
            }
        }
        else if (extension.StartsWith(".xls", StringComparison.Ordinal) || extension.StartsWith(".xlt", StringComparison.Ordinal))
        {
            detail = "Excel · Cell values";
            extracted = AppendSpreadsheet(archive, text, token);
        }
        else if (extension is ".odt" or ".ods" or ".odp" or ".ott" or ".ots" or ".otp")
        {
            detail = "OpenDocument · Text content";
            var entry = archive.GetEntry("content.xml");
            if (entry is not null) { AppendXmlText(entry, text, token, openDocument: true); extracted = true; }
        }
        else if (extension == ".epub")
        {
            detail = "EPUB · Text content";
            var chapters = archive.Entries.Where(entry => entry.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (chapters.Length > 100) text.MarkTruncated();
            foreach (var entry in chapters.Take(100))
            {
                AppendXmlText(entry, text, token, openDocument: true);
                text.Line();
                extracted = true;
                if (text.Full) break;
            }
        }
        if (!extracted)
        {
            detail = $"Archive · {archive.Entries.Count:N0} entries";
            text.Line(Path.GetFileName(path));
            text.Line($"{archive.Entries.Count:N0} entries · {FormatBytes(stream.Length)}");
            text.Line();
            var listed = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (text.Full || listed++ >= 1000) { text.MarkTruncated(); break; }
                text.Line($"{FormatBytes(entry.Length),12}  {entry.FullName}");
            }
        }
        if (text.Length == 0) text.Line("No text content in this document.");
        return new(text.ToString(), detail + (text.Truncated ? " · Partial preview" : ""), false,
            Truncated: text.Truncated);
    }

    private static void EnsureBoundedArchive(FileStream stream, CancellationToken token)
    {
        if (stream.Length > MaximumArchiveBytes) throw new InvalidDataException("Archive preview is limited to 512 MiB.");
        var tailLength = (int)Math.Min(stream.Length, 65_557);
        stream.Position = stream.Length - tailLength;
        var tail = ReadPrefix(stream, tailLength, token);
        var found = false;
        uint centralOffset = 0, centralLength = 0;
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (tail[index] != 0x50 || tail[index + 1] != 0x4b || tail[index + 2] != 5 || tail[index + 3] != 6) continue;
            var entries = BitConverter.ToUInt16(tail, index + 10);
            var commentLength = BitConverter.ToUInt16(tail, index + 20);
            if (index + 22 + commentLength != tail.Length) continue;
            if (entries > MaximumArchiveEntries) throw new InvalidDataException("Too many archive entries.");
            if (BitConverter.ToUInt16(tail, index + 4) != 0 || BitConverter.ToUInt16(tail, index + 6) != 0)
                throw new InvalidDataException("Multi-volume archive preview is unsupported.");
            centralLength = BitConverter.ToUInt32(tail, index + 12);
            centralOffset = BitConverter.ToUInt32(tail, index + 16);
            if (centralLength > 16 * 1024 * 1024 || (ulong)centralOffset + centralLength > (ulong)stream.Length)
                throw new InvalidDataException("The archive directory exceeds the preview budget.");
            found = true;
            break;
        }
        if (!found) throw new InvalidDataException("The archive directory could not be read.");

        // Count actual directory records before ZipArchive allocates objects.
        // An untrusted EOCD count alone can lie about the directory's size.
        stream.Position = centralOffset;
        var centralEnd = (long)centralOffset + centralLength;
        var actualEntries = 0;
        Span<byte> header = stackalloc byte[46];
        while (stream.Position < centralEnd)
        {
            token.ThrowIfCancellationRequested();
            if (centralEnd - stream.Position < header.Length) throw new InvalidDataException("Invalid archive directory.");
            stream.ReadExactly(header);
            if (BitConverter.ToUInt32(header) != 0x02014b50) throw new InvalidDataException("Invalid archive directory.");
            if (++actualEntries > MaximumArchiveEntries) throw new InvalidDataException("Too many archive entries.");
            var variableLength = BitConverter.ToUInt16(header[28..]) + BitConverter.ToUInt16(header[30..])
                + BitConverter.ToUInt16(header[32..]);
            if (stream.Position + variableLength > centralEnd) throw new InvalidDataException("Invalid archive directory.");
            stream.Position += variableLength;
        }
        stream.Position = 0;
    }

    private static XmlReader OpenXml(ZipArchiveEntry entry)
    {
        if (entry.Length > MaximumXmlBytes) throw new InvalidDataException("The document's XML part is too large.");
        return XmlReader.Create(entry.Open(), new XmlReaderSettings
        {
            CloseInput = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlBytes, IgnoreComments = true
        });
    }

    private static string ElementText(XmlReader reader)
    {
        using var content = reader.ReadSubtree();
        var text = new StringBuilder();
        while (content.Read())
            if (content.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                text.Append(content.Value);
        return text.ToString();
    }

    private static void AppendXmlText(ZipArchiveEntry entry, LimitedText target, CancellationToken token,
        bool openDocument = false)
    {
        using var reader = OpenXml(entry);
        while (!target.Full && reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (openDocument)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "script" or "style" or "head"
                    or "scripts" or "automatic-styles" or "binary-data")
                {
                    // ReadSubtree consumes only this destination, preserving
                    // the next paragraph for the outer reader.
                    using var ignored = reader.ReadSubtree();
                    while (ignored.Read()) token.ThrowIfCancellationRequested();
                    continue;
                }
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "s")
                {
                    var spaces = reader.GetAttribute("c", "urn:oasis:names:tc:opendocument:xmlns:text:1.0");
                    target.Add(new string(' ', int.TryParse(spaces, out var number) ? Math.Clamp(number, 1, 4096) : 1));
                }
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "tab") target.Add("\t");
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "line-break" or "br") target.Line();
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                    target.Add(reader.Value);
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "p" or "h" or "tr" or "table-row") target.Line();
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "td" or "table-cell") target.Add("\t");
            }
            else
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t") target.Add(ElementText(reader));
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "tab") target.Add("\t");
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "br" or "cr") target.Line();
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "p" or "tr") target.Line();
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "tc") target.Add("\t");
            }
        }
        if (target.Full) target.MarkTruncated();
    }

    private static bool AppendSpreadsheet(ZipArchive archive, LimitedText target, CancellationToken token)
    {
        var strings = new List<string>();
        var stringBytes = 0;
        if (archive.GetEntry("xl/sharedStrings.xml") is { } shared)
        {
            using var reader = OpenXml(shared);
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si") continue;
                var value = ElementText(reader);
                stringBytes += value.Length;
                if (strings.Count >= 100_000 || stringBytes > MaximumCharacters * 2)
                    throw new InvalidDataException("The shared string table is too large.");
                strings.Add(value);
            }
        }
        var found = false;
        var sheets = archive.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)
            && entry.FullName.EndsWith(".xml", StringComparison.Ordinal)
            && entry.FullName["xl/worksheets/sheet".Length..^4].All(char.IsAsciiDigit))
            .OrderBy(entry => PartNumber(entry.FullName)).ToArray();
        if (sheets.Length > 100) target.MarkTruncated();
        foreach (var sheet in sheets.Take(100))
        {
            target.Line($"Sheet {PartNumber(sheet.FullName)}");
            using var reader = OpenXml(sheet);
            while (!target.Full && reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row") target.Line();
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c") continue;
                var type = reader.GetAttribute("t");
                var address = reader.GetAttribute("r");
                string value = "";
                using (var cell = reader.ReadSubtree())
                {
                    while (cell.Read())
                        if (cell.NodeType == XmlNodeType.Element && cell.LocalName is "v" or "t") value += ElementText(cell);
                }
                if (type == "s" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    && index >= 0 && index < strings.Count) value = strings[index];
                else if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                // Formulas are never evaluated; cached values are shown.
                target.Add($"{address}: {value}\t");
            }
            target.Line();
            found = true;
            if (target.Full) { target.MarkTruncated(); break; }
        }
        return found;
    }

    private static int PartNumber(string name)
    {
        var end = name.LastIndexOf('.');
        var start = end;
        while (start > 0 && char.IsAsciiDigit(name[start - 1])) start--;
        return int.TryParse(name.AsSpan(start, end - start), out var value) ? value : 0;
    }

    private static string ExtractRtf(byte[] bytes, int maxChars, CancellationToken token, out bool truncated)
    {
        // Interpret only the text controls of RTF. Embedded objects/pictures,
        // destinations and field instructions are skipped, never activated.
        var input = Encoding.Latin1.GetString(bytes);
        var target = new LimitedText(maxChars);
        var states = new Stack<(bool Skip, int UnicodeSkip)>();
        var skip = false;
        var unicodeSkip = 1;
        var fallback = 0;
        var encoding = Encoding.GetEncoding(1252);
        for (var index = 0; index < input.Length && !target.Full; index++)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
            var value = input[index];
            if (value == '{')
            {
                if (states.Count >= 256) throw new InvalidDataException("RTF nesting is too deep.");
                states.Push((skip, unicodeSkip));
                continue;
            }
            if (value == '}') { if (states.TryPop(out var state)) (skip, unicodeSkip) = state; continue; }
            if (value is '\r' or '\n') continue;
            if (value != '\\')
            {
                if (fallback > 0) { fallback--; continue; }
                if (!skip) target.Add(encoding.GetString(new[] { (byte)value }));
                continue;
            }
            if (++index >= input.Length) break;
            value = input[index];
            if (value == '*') { skip = true; continue; }
            if (value is '\\' or '{' or '}' or '~' or '_')
            {
                if (fallback > 0) { fallback--; continue; }
                if (!skip) target.Add(value == '~' ? "\u00a0" : value == '_' ? "\u2011" : value.ToString());
                continue;
            }
            if (value == '\'' && index + 2 < input.Length)
            {
                if (byte.TryParse(input.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var encoded))
                {
                    if (fallback > 0) fallback--;
                    else if (!skip) target.Add(encoding.GetString(new[] { encoded }));
                }
                index += 2;
                continue;
            }
            if (!char.IsAsciiLetter(value)) continue;
            var start = index;
            while (index < input.Length && char.IsAsciiLetter(input[index])) index++;
            var word = input[start..index];
            var numberStart = index;
            if (index < input.Length && input[index] == '-') index++;
            while (index < input.Length && char.IsAsciiDigit(input[index])) index++;
            _ = int.TryParse(input.AsSpan(numberStart, index - numberStart), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var number);
            if (index < input.Length && input[index] == ' ') index++;
            index--;
            if (word is "fonttbl" or "colortbl" or "stylesheet" or "info" or "pict" or "object" or "fldinst"
                or "xmlnstbl" or "datastore" or "themedata" or "listtable" or "listoverridetable") { skip = true; continue; }
            if (word == "bin") { index += Math.Clamp(number, 0, input.Length - index - 1); continue; }
            if (word == "ansicpg")
            {
                try { encoding = Encoding.GetEncoding(number); } catch (ArgumentException) { }
            }
            if (word == "uc") unicodeSkip = Math.Clamp(number, 0, 16);
            if (skip) continue;
            if (word == "u") { target.Add(((char)(ushort)number).ToString()); fallback = unicodeSkip; }
            else if (word is "par" or "line") target.Line();
            else if (word == "tab") target.Add("\t");
            else if (word == "emdash") target.Add("\u2014");
            else if (word == "endash") target.Add("\u2013");
            else if (word == "bullet") target.Add("\u2022");
            else if (word is "lquote" or "rquote") target.Add("'");
            else if (word is "ldblquote" or "rdblquote") target.Add("\"");
        }
        truncated = target.Truncated || target.Full;
        return target.ToString();
    }

    private static string SafeCut(string value, int length)
    {
        if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }

    private static string FormatBytes(long value) => value >= 1024L * 1024 * 1024 ? $"{value / (1024d * 1024 * 1024):0.##} GiB"
        : value >= 1024 * 1024 ? $"{value / (1024d * 1024):0.##} MiB" : value >= 1024 ? $"{value / 1024d:0.##} KiB" : $"{value} B";

    private sealed class LimitedText(int limit)
    {
        private readonly StringBuilder _builder = new();
        public bool Truncated { get; private set; }
        public bool Full => _builder.Length >= limit;
        public int Length => _builder.Length;
        public void MarkTruncated() => Truncated = true;
        public void Add(string value)
        {
            if (value.Length > limit - _builder.Length)
            {
                value = SafeCut(value, limit - _builder.Length);
                Truncated = true;
            }
            _builder.Append(value);
        }
        public void Line(string value = "") { Add(value); Add("\n"); }
        public override string ToString() => _builder.ToString();
    }
}
