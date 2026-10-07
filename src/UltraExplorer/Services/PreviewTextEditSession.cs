using System.Security.Cryptography;
using System.Text;

namespace UltraExplorer.Services;

/// <summary>Preserves the original encoding and rejects saving over another program's edits.</summary>
internal sealed class PreviewTextEditSession
{
    private readonly string _path;
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;
    private byte[] _fingerprint;
    internal const int MaximumBytes = 2 * 1024 * 1024;

    private PreviewTextEditSession(string path, Encoding encoding, byte[] preamble, byte[] bytes)
    {
        _path = path;
        _encoding = encoding;
        _preamble = preamble;
        _fingerprint = SHA256.HashData(bytes);
        Text = encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length);
    }

    internal string Text { get; private set; }

    internal static async Task<PreviewTextEditSession> OpenAsync(string path, string encodingName, bool hasBom, CancellationToken token)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if ((File.GetAttributes(DocumentPreviewService.LiteralPath(path)) & FileAttributes.ReadOnly) != 0)
            throw new IOException("The file is read only. Use Open with to edit it in another application.");
        var encoding = Encoding.GetEncoding(encodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var bytes = await ReadBoundedAsync(path, token);
        byte[] preamble = hasBom ? encoding.GetPreamble() : [];
        // Encoding.GetEncoding("utf-8") can omit the BOM even when the source has one.
        if (hasBom && encoding.CodePage == 65001) preamble = [0xef, 0xbb, 0xbf];
        if (preamble.Length > 0 && !bytes.AsSpan().StartsWith(preamble))
            throw new IOException("The file changed while its preview was loading. Close and reopen the preview.");
        return new PreviewTextEditSession(DocumentPreviewService.LiteralPath(path), encoding, preamble, bytes);
    }

    internal async Task SaveAsync(string text, CancellationToken token)
    {
        var content = _encoding.GetBytes(text);
        if (content.Length + _preamble.Length > MaximumBytes) throw new IOException("The mini editor supports files up to 2 MB.");
        var bytes = new byte[content.Length + _preamble.Length];
        _preamble.CopyTo(bytes, 0);
        content.CopyTo(bytes, _preamble.Length);
        // Deny ordinary concurrent writers through the commit. Delete sharing
        // is necessary for atomic replacement; existing readers can continue.
        using var guard = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 16384, true);
        var current = await ReadBoundedAsync(guard, token);
        if (!CryptographicOperations.FixedTimeEquals(_fingerprint, SHA256.HashData(current)))
            throw new IOException("Another program changed this file. Your edits are still here; copy them before reopening the preview.");
        var temporary = Path.Combine(Path.GetDirectoryName(_path)!, ".ultra-preview-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            token.ThrowIfCancellationRequested();
            // Detect an editor which saved by renaming/replacing the path
            // while our guarded original file handle stayed open.
            var atPath = await ReadBoundedAsync(_path, token);
            if (!CryptographicOperations.FixedTimeEquals(_fingerprint, SHA256.HashData(atPath)))
                throw new IOException("Another program replaced this file. Your edits are still here; reopen the preview before saving.");
            // Atomic replacement preserves the existing file's security information.
            File.Replace(temporary, _path, null, ignoreMetadataErrors: false);
            _fingerprint = SHA256.HashData(bytes);
            Text = text;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        using var file = new FileStream(DocumentPreviewService.LiteralPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384, true);
        return await ReadBoundedAsync(file, token);
    }

    private static async Task<byte[]> ReadBoundedAsync(FileStream file, CancellationToken token)
    {
        if (file.Length > MaximumBytes) throw new IOException("The mini editor supports files up to 2 MB.");
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await file.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > MaximumBytes) throw new IOException("The file grew beyond the mini editor's 2 MB limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
