using System.Net.Http.Headers;
using System.Net;
using CG.Web.MegaApiClient;

namespace MegaDownloaderNext.Core.Transfers;

public sealed class HttpMegaDownloadEngine
{
    private readonly HttpClient _httpClient;

    public HttpMegaDownloadEngine(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task DownloadAsync(
        string downloadUrl,
        byte[] fileKey,
        string outputPath,
        long totalSize,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(outputPath);
        long startOffset = 0;
        
        if (fileInfo.Exists)
        {
            startOffset = fileInfo.Length;
            if (startOffset == totalSize)
            {
                progress.Report(new DownloadProgress(totalSize, totalSize));
                return;
            }

            if (startOffset > totalSize)
            {
                startOffset = 0;
            }
        }

        // Mega keys are: 16 bytes key, 8 bytes nonce, 8 bytes MAC
        if (fileKey.Length < 24)
        {
            throw new InvalidDataException("MEGA file key is incomplete.");
        }

        var aesKey = new byte[16];
        var nonce = new byte[8];
        Array.Copy(fileKey, 0, aesKey, 0, 16);
        Array.Copy(fileKey, 16, nonce, 0, 8);

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        if (startOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(startOffset, null);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (startOffset > 0)
        {
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                startOffset = 0;
            }
            else if (response.Content.Headers.ContentRange?.From != startOffset)
            {
                throw new IOException("The server returned an unexpected resume range.");
            }
        }

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(
            outputPath,
            startOffset > 0 ? FileMode.OpenOrCreate : FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        fileStream.SetLength(startOffset);
        fileStream.Seek(startOffset, SeekOrigin.Begin);

        var buffer = new byte[128 * 1024]; // 128KB buffer
        int bytesRead;
        long currentOffset = startOffset;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            Mega.MegaCrypto.DecryptInPlace(buffer, bytesRead, aesKey, nonce, currentOffset);
            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            
            currentOffset += bytesRead;
            progress.Report(new DownloadProgress(currentOffset, totalSize));
        }

        if (currentOffset != totalSize)
        {
            throw new IOException($"Download ended at {currentOffset} bytes, expected {totalSize} bytes.");
        }
    }
}
