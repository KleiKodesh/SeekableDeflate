namespace SeekableDeflate;

/// <summary>
/// Chooses where a part's checkpoints go while it is inflated once: one per <see cref="SpanBytes"/> of
/// output, snapped to the block boundary of the region that needs the fewest window bytes. Every block
/// boundary in the first half of a region is captured as a candidate; the inflater seals each one 32 KB
/// later and reports how many window bytes the following output copied from it
/// (<see cref="DeflateCheckpoint.UsedWindowBytes"/>). A history-free boundary (0 bytes) is taken at once;
/// otherwise the smallest window wins when the region's candidates are all sealed. Measured on a 91 MB
/// document.xml: 113 of 774 boundaries are history-free, the median boundary needs 6.5 KB of its 32 KB.
/// <see cref="Checkpoints"/> grows as the pass goes (ascending offsets), so a view opened over it can
/// already seek into the part inflated so far — a region still being decided is reached from the
/// previous checkpoint.
/// </summary>
public sealed class DeflateCheckpointSelector
{
    public const long DefaultSpanBytes = 2L << 20;

    private readonly long _spanBytes;
    private readonly long _searchBytes;
    private readonly List<DeflateCheckpoint> _candidates = new();   // the open region's candidates, offset order
    private readonly List<DeflateCheckpoint> _sealed = new();
    private long _regionStart;         // output offset the open (or next) region starts at
    private bool _regionClosed;        // no more candidates: the output passed the search window
    private bool _regionDecided = true;

    public List<DeflateCheckpoint> Checkpoints { get; } = new();

    /// <summary>
    /// Raised for every captured candidate, chosen or not, at its block boundary — before any output past it is handed
    /// out. An index pass that needs per-checkpoint state (the first block after it, counters) records it for each
    /// candidate and keeps the records of the chosen ones.
    /// </summary>
    public event Action<DeflateCheckpoint>? CandidateCaptured;

    public long SpanBytes => _spanBytes;

    public DeflateCheckpointSelector(DeflateCheckpointInflater inflater, long spanBytes = DefaultSpanBytes)
    {
        if (spanBytes <= 0) throw new ArgumentOutOfRangeException(nameof(spanBytes));
        _spanBytes = spanBytes;
        _searchBytes = Math.Max(DeflateCheckpoint.WindowSize, spanBytes / 2);
        inflater.BlockBoundaryReached += OnBlockBoundary;
        inflater.CheckpointSealed += OnSealed;
    }

    private void OnBlockBoundary(DeflateCheckpointInflater inflater)
    {
        if (inflater.IsPastFinalBlock) return;
        long offset = inflater.TotalOut;
        if (_regionDecided)
        {
            if (offset < _regionStart) return;
            _regionDecided = false;
            _regionClosed = false;
            _candidates.Clear();
            _sealed.Clear();
        }
        if (_regionClosed) return;
        if (_candidates.Count > 0 && offset >= _regionStart + _searchBytes)
        {
            _regionClosed = true;        // the candidates are in; the decision falls when the last one is sealed
            DecideIfComplete();
            return;
        }
        DeflateCheckpoint candidate = inflater.CaptureCheckpoint();
        _candidates.Add(candidate);
        CandidateCaptured?.Invoke(candidate);
        if (candidate.IsSealed) OnSealed(candidate);     // an empty window (offset 0) is sealed on capture
    }

    private void OnSealed(DeflateCheckpoint checkpoint)
    {
        if (_regionDecided || !_candidates.Contains(checkpoint)) return;
        _sealed.Add(checkpoint);
        if (checkpoint.UsedWindowBytes == 0) { Choose(checkpoint); return; }
        if (_regionClosed) DecideIfComplete();
    }

    private void DecideIfComplete()
    {
        if (_sealed.Count < _candidates.Count) return;
        DeflateCheckpoint best = _sealed[0];
        foreach (DeflateCheckpoint candidate in _sealed)
            if (candidate.UsedWindowBytes >= 0 && candidate.UsedWindowBytes < best.UsedWindowBytes) best = candidate;
        Choose(best);
    }

    private void Choose(DeflateCheckpoint checkpoint)
    {
        Checkpoints.Add(checkpoint);
        _regionStart = checkpoint.UncompressedOffset + _spanBytes;
        _regionDecided = true;
        _candidates.Clear();
        _sealed.Clear();
    }

    /// <summary>At the end of the stream (every candidate is sealed by then): decides a region still open.</summary>
    public void Finish()
    {
        if (_regionDecided || _candidates.Count == 0) return;
        _regionClosed = true;
        DecideIfComplete();
        if (!_regionDecided) Choose(_candidates[0]);   // a reader that stopped early: the first boundary, sealed whole
    }
}
