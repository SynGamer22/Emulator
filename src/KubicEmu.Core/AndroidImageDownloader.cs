using System.Net;
using System.Security.Cryptography;

namespace KubicEmu.Core;

public sealed class AndroidImageDownloader(HttpClient? client = null)
{
    private readonly HttpClient _client = client ?? new HttpClient();

    public async Task DownloadVerifiedAsync(
        Uri source,
        string destinationPath,
        string expectedSha256,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (source.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Android images must be downloaded over HTTPS.", nameof(source));
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit)) throw new ArgumentException("Enter the publisher-provided 64-character SHA-256 hash.", nameof(expectedSha256));
        if (File.Exists(destinationPath)) throw new IOException("The destination already exists; it was not overwritten.");

        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var partialPath = fullPath + ".partial";
        var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (offset > 0 && !append && response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"Server returned {response.StatusCode} for the resumed download.");
        await using (var output = new FileStream(partialPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var buffer = new byte[128 * 1024];
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                progress?.Report(output.Length);
            }
        }

        if (!await EmulatorController.VerifySha256Async(partialPath, expectedSha256, cancellationToken))
        {
            File.Delete(partialPath);
            throw new InvalidDataException("The downloaded image failed SHA-256 verification. Check the URL and publisher hash.");
        }
        File.Move(partialPath, fullPath);
    }
}