namespace RaceTrace.Api.Infrastructure.Telemetry;

// Counts bytes in flight; never retains payloads or owns the underlying stream.
internal sealed class CountingStream(Stream inner, Action<int> count) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override int Read(byte[] buffer, int offset, int length)
    {
        var read = inner.Read(buffer, offset, length);
        count(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        count(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        count(read);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int length,
        CancellationToken cancellationToken
    )
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, length), cancellationToken);
        count(read);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int length)
    {
        inner.Write(buffer, offset, length);
        count(length);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        count(buffer.Length);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        await inner.WriteAsync(buffer, cancellationToken);
        count(buffer.Length);
    }

    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int length,
        CancellationToken cancellationToken
    )
    {
        await inner.WriteAsync(buffer.AsMemory(offset, length), cancellationToken);
        count(length);
    }
}
