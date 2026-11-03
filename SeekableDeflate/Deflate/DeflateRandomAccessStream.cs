namespace SeekableDeflate;

/// <summary>
/// A seekable, read-only view of the uncompressed content of a raw deflate region, backed by a
/// list of checkpoints. A sequential read continues the running inflater; a seek elsewhere resumes
/// from the nearest earlier checkpoint and skips forward (at most one checkpoint span, ~1 MB). A short seek back,
/// into the output the inflater still holds as its history (at least the last 32 KB), is served from that
/// history: no resume, nothing inflated again.
/// </summary>
public sealed class DeflateRandomAccessStream : Stream
{
    private readonly DeflateCheckpointInflater _inflater;
    private readonly IReadOnlyList<DeflateCheckpoint> _checkpoints;   // ascending UncompressedOffset
    private readonly long _length;
    private readonly byte[] _skipBuffer = new byte[1 << 16];
    private long _position;
    private bool _isInflaterSeated;   // false until the first read / after a failed resume

    /// <summary>How many times a read resumed from a checkpoint (a measurement counter).</summary>
    public int CheckpointResumeCount { get; private set; }

    /// <summary>Every byte inflated since this stream was made, resumes and skips included (a measurement counter).</summary>
    public long BytesInflated => _inflater.BytesInflated;

    private static long _processCheckpointResumes, _processBytesInflated;

    /// <summary>Checkpoint resumes of every stream in the process (a measurement counter for hosts that do not hold the stream).</summary>
    public static long ProcessCheckpointResumeCount => Interlocked.Read(ref _processCheckpointResumes);

    /// <summary>Bytes inflated by every stream in the process (a measurement counter).</summary>
    public static long ProcessBytesInflated => Interlocked.Read(ref _processBytesInflated);

    public DeflateRandomAccessStream(Stream compressedInput, long dataStart, long dataLength,
                                     long uncompressedLength, IReadOnlyList<DeflateCheckpoint> checkpoints)
    {
        _inflater = new DeflateCheckpointInflater(compressedInput, dataStart, dataLength);
        _checkpoints = checkpoints;
        _length = uncompressedLength;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _length) throw new ArgumentOutOfRangeException(nameof(value));
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length) return 0;
        count = (int)Math.Min(count, _length - _position);
        if (_isInflaterSeated && _position < _inflater.TotalOut && _position >= _inflater.HistoryStart)
        {
            int copied = _inflater.CopyFromHistory(_position, buffer, offset, count);   // a short seek back: still in the history
            _position += copied;
            return copied;
        }
        long inflatedBefore = _inflater.BytesInflated;
        SeatInflaterAt(_position);
        int read = _inflater.Read(buffer, offset, count);
        _position += read;
        Interlocked.Add(ref _processBytesInflated, _inflater.BytesInflated - inflatedBefore);
        return read;
    }

    private void SeatInflaterAt(long target)
    {
        if (_isInflaterSeated && _inflater.TotalOut == target) return;

        if (!_isInflaterSeated || _inflater.TotalOut > target || target - _inflater.TotalOut > NearestSpanBefore(target))
        {
            DeflateCheckpoint checkpoint = _checkpoints[NearestCheckpointIndex(target)];
            _inflater.ResumeFrom(checkpoint);
            _isInflaterSeated = true;
            CheckpointResumeCount++;
            Interlocked.Increment(ref _processCheckpointResumes);
        }
        // Skip forward to the target (never more than one checkpoint span).
        while (_inflater.TotalOut < target)
        {
            int toSkip = (int)Math.Min(_skipBuffer.Length, target - _inflater.TotalOut);
            if (_inflater.Read(_skipBuffer, 0, toSkip) == 0) throw new EndOfStreamException("Deflate stream ended before the requested offset.");
        }
    }

    private int NearestCheckpointIndex(long target)
    {
        int low = 0, high = _checkpoints.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (_checkpoints[middle].UncompressedOffset <= target) low = middle; else high = middle - 1;
        }
        return low;
    }

    /// <summary>How far forward from the current position is still cheaper than jumping to a checkpoint.</summary>
    private long NearestSpanBefore(long target)
        => target - _checkpoints[NearestCheckpointIndex(target)].UncompressedOffset;

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
