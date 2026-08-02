using System.Net;
using System.Security.Cryptography;
using System.Text;
using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class HttpAddonPackageDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WindowsGSH.AddonDownloaderTests", Guid.NewGuid().ToString("N"));

    public HttpAddonPackageDownloaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task DownloadAsync_rejects_response_whose_content_length_header_exceeds_the_limit()
    {
        var body = new byte[100];
        var handler = new FakeHandler(_ => CreateResponse(body, declareContentLength: true));
        var downloader = new HttpAddonPackageDownloader(new HttpClient(handler), maxDownloadBytes: 50);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            downloader.DownloadAsync(new Uri("https://example.invalid/addon.zip"), Path.Combine(_root, "out.bin"), CancellationToken.None));

        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAsync_rejects_stream_that_exceeds_the_limit_when_content_length_is_absent()
    {
        // A real chunked-transfer response has no Content-Length, so the pre-check can't catch
        // an oversized download - the streaming running-total check must catch it instead. A
        // seekable MemoryStream would let StreamContent compute Content-Length on its own
        // (defeating the point of this test), so wrap it in a non-seekable stream to force
        // HttpClient down the same "length unknown" path a chunked response takes.
        var body = new byte[100];
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableStream(new MemoryStream(body)))
            };
            return response;
        });
        var downloader = new HttpAddonPackageDownloader(new HttpClient(handler), maxDownloadBytes: 50);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            downloader.DownloadAsync(new Uri("https://example.invalid/addon.zip"), Path.Combine(_root, "out.bin"), CancellationToken.None));

        Assert.Contains("exceeded the maximum size", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAsync_allows_a_download_within_the_limit_and_returns_its_hash()
    {
        var body = Encoding.UTF8.GetBytes("addon package contents");
        var handler = new FakeHandler(_ => CreateResponse(body, declareContentLength: true));
        var downloader = new HttpAddonPackageDownloader(new HttpClient(handler), maxDownloadBytes: 1024);
        var targetPath = Path.Combine(_root, "out.bin");

        var result = await downloader.DownloadAsync(new Uri("https://example.invalid/addon.zip"), targetPath, CancellationToken.None);

        Assert.Equal(body, await File.ReadAllBytesAsync(targetPath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(body)), result.Sha256);
    }

    [Fact]
    public async Task DownloadAsync_surfaces_non_success_status_codes()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var downloader = new HttpAddonPackageDownloader(new HttpClient(handler), maxDownloadBytes: 1024);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            downloader.DownloadAsync(new Uri("https://example.invalid/missing.zip"), Path.Combine(_root, "out.bin"), CancellationToken.None));
    }

    private static HttpResponseMessage CreateResponse(byte[] body, bool declareContentLength)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(body))
        };
        if (declareContentLength)
        {
            response.Content.Headers.ContentLength = body.Length;
        }
        return response;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }

    /// <summary>Wraps a seekable stream to look non-seekable, so StreamContent can't compute Content-Length from it - mimicking a real chunked-transfer response for tests.</summary>
    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
