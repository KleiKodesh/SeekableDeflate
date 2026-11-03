using System.IO.Compression;
using SeekableDeflate;

namespace SeekableDeflate.Tests;

/// <summary>
/// The managed inflater against .NET's DeflateStream output: full-stream byte equality, then the
/// checkpoint mechanism — random seeks through <see cref="DeflateRandomAccessStream"/> must return
/// the same bytes as the original at every offset, including offsets just after a checkpoint and
/// reads that straddle checkpoints.
/// </summary>
public static class DeflateInflaterRoundTripTest
{
    public static void Run(int megabytes)
    {
        TestReport.Step($"Deflate round trip on {megabytes} MB of mixed text");
        byte[] original = MakeSampleText(megabytes << 20);
        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(original, 0, original.Length);
            compressed = buffer.ToArray();
        }
        Console.WriteLine($"   compressed to {compressed.Length / 1024} KB");

        // 1. sequential inflate with checkpoints every 256 KB
        var checkpoints = new List<DeflateCheckpoint>();
        var inflated = new byte[original.Length];
        TestReport.Timed("sequential inflate", () =>
        {
            using var input = new MemoryStream(compressed);
            var inflater = new DeflateCheckpointInflater(input, 0, compressed.Length);
            long nextCheckpoint = 0;
            inflater.BlockBoundaryReached += source =>
            {
                if (source.TotalOut < nextCheckpoint) return;
                checkpoints.Add(source.CaptureCheckpoint());
                nextCheckpoint = source.TotalOut + (256 << 10);
            };
            int filled = 0;
            while (filled < inflated.Length)
            {
                int read = inflater.Read(inflated, filled, Math.Min(7919, inflated.Length - filled));   // odd sizes cross symbol boundaries
                if (read == 0) break;
                filled += read;
            }
            TestReport.Check(filled == original.Length, $"inflated length {filled} == {original.Length}");
        });
        TestReport.Check(inflated.AsSpan().SequenceEqual(original), "sequential inflate matches the original byte for byte");
        TestReport.Check(checkpoints.Count > 2, $"{checkpoints.Count} checkpoints captured");
        TestReport.Check(checkpoints[0].UncompressedOffset == 0, "first checkpoint is at offset 0");
        // the loop above stops at the last byte without reading the final block header, so the last checkpoint may seal on first use
        TestReport.Check(checkpoints.Take(checkpoints.Count - 1).All(c => c.IsSealed), "every checkpoint is sealed (sparse window) 32 KB after its capture");
        CheckpointSelectorTest.Run(original, compressed);
        long sealedBytes = checkpoints.Sum(c => (long)c.CompressedWindow.Length), rawBytes = checkpoints.Sum(c => (long)c.WindowLength);
        Console.WriteLine($"   windows: raw {rawBytes / 1024} KB -> sealed sparse {sealedBytes / 1024} KB");
        // the random seeks below run over the sealed sparse windows (round-tripped through storage like a sidecar does) and are
        // compared byte for byte with the original — the same check a full-window seek had to pass
        // round-trip the sealed form through storage like a sidecar does
        var stored = checkpoints.Select(c => new DeflateCheckpoint(c.CompressedBytePosition, c.LeftoverBitCount, c.LeftoverBits, c.UncompressedOffset, c.WindowLength, c.CompressedWindow)).ToList();
        checkpoints = stored;

        // 2. random seeks through the stream
        using var seekInput = new MemoryStream(compressed);
        var stream = new DeflateRandomAccessStream(seekInput, 0, compressed.Length, original.Length, checkpoints);
        var random = new Random(7);
        bool allMatch = true;
        int seeks = 300;
        TestReport.Timed($"{seeks} random seeks + 4 KB reads", () =>
        {
            var slice = new byte[4096];
            for (int i = 0; i < seeks; i++)
            {
                long offset = i % 3 == 0 && i / 3 < checkpoints.Count
                    ? checkpoints[i / 3].UncompressedOffset + random.Next(3)                  // right after a checkpoint
                    : random.NextInt64(0, original.Length - slice.Length);
                stream.Position = offset;
                int read = ReadFully(stream, slice);
                if (!slice.AsSpan(0, read).SequenceEqual(original.AsSpan((int)offset, read))) { allMatch = false; Console.WriteLine($"   mismatch at {offset}"); }
            }
        });
        TestReport.Check(allMatch, "every random read matches the original");

        // 3. a read straddling a checkpoint boundary
        if (checkpoints.Count > 2)
        {
            long boundary = checkpoints[2].UncompressedOffset;
            var straddle = new byte[64 << 10];
            stream.Position = boundary - straddle.Length / 2;
            int read = ReadFully(stream, straddle);
            TestReport.Check(straddle.AsSpan(0, read).SequenceEqual(original.AsSpan((int)(boundary - straddle.Length / 2), read)), "read across a checkpoint boundary matches");
        }

        RunBackSeekChecks(original, compressed, checkpoints);
    }

    /// <summary>
    /// Back-seeks into the inflater's history (the last 32 KB it produced) are served from it: no checkpoint resume,
    /// nothing inflated again. Distances at the edges (0, 1, 32 KB - 1, 32 KB) must not resume; 32 KB + 1 and a seek
    /// back past the checkpoint a resume started from may resume — every read must match the original either way.
    /// </summary>
    private static void RunBackSeekChecks(byte[] original, byte[] compressed, IReadOnlyList<DeflateCheckpoint> checkpoints)
    {
        TestReport.Step("Back-seeks served from the inflater's history");
        const int Window = DeflateCheckpoint.WindowSize;
        var slice = new byte[4096];
        bool Matches(DeflateRandomAccessStream stream, long offset)
        {
            stream.Position = offset;
            int read = ReadFully(stream, slice);
            return read > 0 && slice.AsSpan(0, read).SequenceEqual(original.AsSpan((int)offset, read));
        }

        // a target far enough into a span that 64 KB were produced since the checkpoint the read resumed from
        DeflateCheckpoint seat = checkpoints[Math.Min(1, checkpoints.Count - 1)];
        long target = seat.UncompressedOffset + 3 * Window;
        foreach (int distance in new[] { 0, 1, Window - 1, Window, Window + 1 })
        {
            using var input = new MemoryStream(compressed);
            var stream = new DeflateRandomAccessStream(input, 0, compressed.Length, original.Length, checkpoints);
            stream.Position = target - 2 * Window;
            ReadFully(stream, new byte[2 * Window]);          // the inflater now stands at target, 64 KB of history behind it
            int resumes = stream.CheckpointResumeCount;
            long inflated = stream.BytesInflated;
            bool matches = Matches(stream, target - distance);
            TestReport.Check(matches, $"back {distance} bytes: the read matches the original");
            if (distance <= Window)
                TestReport.Check(stream.CheckpointResumeCount == resumes && stream.BytesInflated - inflated <= Math.Max(0, slice.Length - distance) + 258,   // only the part of the read past the history (+ at most one match of overshoot)
                    $"back {distance} bytes: served from the history (resumes +{stream.CheckpointResumeCount - resumes}, inflated +{stream.BytesInflated - inflated} B)");
        }

        // offset 0: read the first bytes, seek back to the very start
        {
            using var input = new MemoryStream(compressed);
            var stream = new DeflateRandomAccessStream(input, 0, compressed.Length, original.Length, checkpoints);
            ReadFully(stream, new byte[1000]);
            int resumes = stream.CheckpointResumeCount;
            TestReport.Check(Matches(stream, 0) && stream.CheckpointResumeCount == resumes, "back to offset 0: served from the history, matches");
            TestReport.Check(Matches(stream, 1) && stream.CheckpointResumeCount == resumes, "then offset 1: served from the history, matches");
        }

        // right after a resume the history starts at the checkpoint (its window may be sparse): a seek before it resumes
        if (checkpoints.Count > 2)
        {
            using var input = new MemoryStream(compressed);
            var stream = new DeflateRandomAccessStream(input, 0, compressed.Length, original.Length, checkpoints);
            long checkpoint = checkpoints[2].UncompressedOffset;
            TestReport.Check(Matches(stream, checkpoint + 10), "read just after a checkpoint matches");
            int resumes = stream.CheckpointResumeCount;
            TestReport.Check(Matches(stream, checkpoint) && stream.CheckpointResumeCount == resumes, "back to the resumed checkpoint: from the history, matches");
            TestReport.Check(Matches(stream, checkpoint - 1), "back one byte before the resumed checkpoint (sparse window, not history): matches");
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read <= 0) break;
            filled += read;
        }
        return filled;
    }

    private static byte[] MakeSampleText(int length)
    {
        var random = new Random(42);
        var text = new MemoryStream(length);
        string[] words = ["<w:p>", "<w:r>", "<w:t>", "בראשית", "ברא", "אלהים", "the", "quick", "brown", "fox", "</w:t></w:r></w:p>", "0123456789"];
        using var writer = new StreamWriter(text, System.Text.Encoding.UTF8, 1 << 16, leaveOpen: true);
        while (text.Length < length)
        {
            writer.Write(words[random.Next(words.Length)]);
            writer.Write(random.Next(1000) == 0 ? new string((char)('a' + random.Next(26)), random.Next(3000)) : " ");   // some incompressible-ish + long runs
            if (random.Next(500) == 0) writer.Flush();
        }
        writer.Flush();
        return text.ToArray().AsSpan(0, length).ToArray();
    }
}
