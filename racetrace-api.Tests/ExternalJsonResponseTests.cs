using System.Net;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Tests;

public sealed class ExternalJsonResponseTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsOversizedBodyWithOrWithoutContentLength(bool lengthKnown)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new RepeatingStream(ExternalJsonResponse.MaximumBytes + 1, lengthKnown))
        };
        var error = await Assert.ThrowsAsync<ApiException>(() => ExternalJsonResponse.ReadAsync(response, CancellationToken.None));
        Assert.Equal("UPSTREAM_RESPONSE_TOO_LARGE", error.Code);
    }

    [Fact]
    public async Task KeepsUtf8ProviderJsonAndRemovesTheOptionalBom()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("\uFEFF{\"name\":\"Żółć\"}")
        };
        Assert.Equal("{\"name\":\"Żółć\"}", await ExternalJsonResponse.ReadAsync(response, CancellationToken.None));
    }

    private sealed class RepeatingStream(int length, bool seekable) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? length : throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = Math.Min(count, length - _position);
            Array.Fill(buffer, (byte)'x', offset, available);
            _position += available;
            return available;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
