namespace SeekableDeflate;

/// <summary>Read-only seekable window over a stored (uncompressed) zip entry — a plain byte range of the file.</summary>
public sealed class ZipStoredEntryStream : Stream
{
    private readonly Stream _file;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public ZipStoredEntryStream(Stream file, long start, long length)
    {
        _file = file;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = Math.Max(0, Math.Min(_length, value)); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        count = (int)Math.Min(count, _length - _position);
        if (count <= 0) return 0;
        _file.Position = _start + _position;
        int read = _file.Read(buffer, offset, count);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
