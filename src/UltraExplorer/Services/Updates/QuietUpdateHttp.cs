using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UltraExplorer.Services.Updates;

internal sealed class QuietUpdateHttp : IDisposable
{
    internal const string ArchiveName = "UltraExplorer-win-x64.zip";
    internal const string ChecksumsName = "SHA256SUMS.txt";
    internal const long MaximumPackageSize = 1_073_741_824;
    private static readonly Uri ReleasesUri = new("https://api.github.com/repos/So2K/UltraExplorer/releases?per_page=100");
    private readonly HttpClient _client;

    internal sealed record Release(UpdateVersion Version, Uri ZipUrl, long Size, string? Digest, Uri? SumsUrl);

    public QuietUpdateHttp(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false })
            { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("UltraExplorer-QuietUpdates/1.0");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<Release?> FindAsync(UpdateVersion installed, CancellationToken token)
    {
        using var response = await SendAsync(ReleasesUri, null, true, token).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, 2_097_152, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid releases response.");
        Release? newest = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Bool(item, "draft")) continue;
            var tag = String(item, "tag_name");
            var version = UpdateVersion.Parse(tag, tag: true);
            if (version is null || version.CompareTo(installed) <= 0 || (!installed.IsPrerelease && (version.IsPrerelease || Bool(item, "prerelease")))) continue;
            if (newest is not null && version.CompareTo(newest.Version) <= 0) continue;
            if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            Uri? zip = null; Uri? sums = null; string? digest = null; long size = 0; var duplicates = false;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = String(asset, "name");
                if (name != ArchiveName && name != ChecksumsName) continue;
                var url = String(asset, "browser_download_url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsRepositoryAsset(uri, tag!, name)) continue;
                if (name == ArchiveName)
                {
                    duplicates |= zip is not null;
                    zip = uri;
                    if (!asset.TryGetProperty("size", out var length) || length.ValueKind != JsonValueKind.Number || !length.TryGetInt64(out size)) size = 0;
                    var rawDigest = String(asset, "digest");
                    if (rawDigest is not null)
                    {
                        if (!rawDigest.StartsWith("sha256:", StringComparison.Ordinal) || !IsHash(rawDigest[7..])) { duplicates = true; continue; }
                        digest = rawDigest[7..].ToLowerInvariant();
                    }
                }
                else { duplicates |= sums is not null; sums = uri; }
            }
            if (duplicates || zip is null || size <= 0 || size >= MaximumPackageSize || (digest is null && sums is null)) continue;
            newest = new Release(version, zip, size, digest, sums);
        }
        return newest;
    }

    public async Task<string> ExpectedHashAsync(Release release, CancellationToken token)
    {
        string? sumHash = null;
        if (release.SumsUrl is not null)
        {
            using var response = await SendAsync(release.SumsUrl, null, false, token).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(await ReadBoundedAsync(response, 131_072, token).ConfigureAwait(false));
            foreach (var line in text.TrimStart('\uFEFF').Split('\n'))
            {
                var fields = line.TrimEnd('\r').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 2 && fields[1].TrimStart('*') == ArchiveName)
                {
                    if (sumHash is not null || !IsHash(fields[0])) throw new InvalidDataException("Invalid archive checksum.");
                    sumHash = fields[0].ToLowerInvariant();
                }
            }
            if (sumHash is null) throw new InvalidDataException("Archive checksum is missing.");
        }
        if (release.Digest is not null && sumHash is not null && release.Digest != sumHash) throw new InvalidDataException("Release checksums disagree.");
        return sumHash ?? release.Digest ?? throw new InvalidDataException("Archive checksum is missing.");
    }

    public Task<HttpResponseMessage> DownloadAsync(Uri uri, long offset, CancellationToken token) => SendAsync(uri, offset > 0 ? offset : null, false, token);

    private async Task<HttpResponseMessage> SendAsync(Uri uri, long? offset, bool api, CancellationToken token)
    {
        var current = uri;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (!IsSafeTransport(current, api)) throw new InvalidDataException("Untrusted update URL.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (offset is > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            // Also detect a handler accidentally configured for automatic redirects.
            if (response.RequestMessage?.RequestUri is { } final && !IsSafeTransport(final, api))
            { response.Dispose(); throw new InvalidDataException("Untrusted update redirect."); }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirect == 5) throw new InvalidDataException("Invalid update redirect.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            if (!response.IsSuccessStatusCode) { var status = response.StatusCode; response.Dispose(); throw new HttpRequestException("Update request failed.", null, status); }
            return response;
        }
        throw new InvalidDataException("Too many update redirects.");
    }

    private static bool IsSafeTransport(Uri uri, bool api)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        if (api) return uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == ReleasesUri.AbsolutePath;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return uri.AbsolutePath.StartsWith("/So2K/UltraExplorer/releases/download/", StringComparison.Ordinal);
        return uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRepositoryAsset(Uri uri, string tag, string name) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath == $"/So2K/UltraExplorer/releases/download/{Uri.EscapeDataString(tag)}/{name}";

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static string? String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Bool(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Update metadata is too large.");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16_384];
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > maximum) throw new InvalidDataException("Update metadata is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public void Dispose() => _client.Dispose();
}
