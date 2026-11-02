using System.IO.Compression;
using SeekableDeflate;

namespace SeekableDeflate.Tests;

/// <summary>
/// The checkpoint selector + compact table: checkpoints chosen every ~span bytes at the thriftiest block
/// boundary, written as a table with one window store, read back, and every random seek through the
/// read-back checkpoints must return the original bytes (sparse windows, dropped history-free windows,
/// grouped streams all exercised).
/// </summary>
public static class CheckpointSelectorTest
{
    public static void Run(byte[] original, byte[] compressed)
    {
        TestReport.Step("Checkpoint selector + compact checkpoint table");
        List<DeflateCheckpoint> checkpoints;
        using (var input = new MemoryStream(compressed))
        {
            var inflater = new DeflateCheckpointInflater(input, 0, compressed.Length);
            var selector = new DeflateCheckpointSelector(inflater, spanBytes: 512 << 10);
            var sink = new byte[1 << 16];
            while (inflater.Read(sink, 0, sink.Length) > 0) { }
            selector.Finish();
            checkpoints = selector.Checkpoints;
        }
        TestReport.Check(checkpoints.Count >= original.Length / (512 << 10) / 2, $"{checkpoints.Count} checkpoints chosen over {original.Length >> 20} MB");
        TestReport.Check(checkpoints[0].UncompressedOffset == 0, "first checkpoint at offset 0");
        bool ascending = true;
        for (int i = 1; i < checkpoints.Count; i++) if (checkpoints[i].UncompressedOffset <= checkpoints[i - 1].UncompressedOffset) ascending = false;
        TestReport.Check(ascending, "checkpoints ascend");
        TestReport.Check(checkpoints.All(c => c.IsSealed && c.UsedWindowBytes >= 0), "every chosen checkpoint is sealed sparse with a known window use");
        Console.WriteLine($"   window bytes used: " + string.Join(",", checkpoints.Select(c => c.UsedWindowBytes)));

        byte[] table;
        using (var buffer = new MemoryStream())
        {
            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true)) DeflateCheckpointTable.Write(writer, checkpoints, groupSize: 3);
            table = buffer.ToArray();
        }
        Console.WriteLine($"   table {table.Length:N0} bytes for {checkpoints.Count} checkpoints (per-checkpoint sealed windows: {checkpoints.Sum(c => (long)c.CompressedWindow.Length):N0} bytes)");
        List<DeflateCheckpoint> read;
        using (var reader = new BinaryReader(new MemoryStream(table))) read = DeflateCheckpointTable.Read(reader);
        TestReport.Check(read.Count == checkpoints.Count && read.Zip(checkpoints, (a, b) => a.UncompressedOffset == b.UncompressedOffset && a.CompressedBytePosition == b.CompressedBytePosition && a.WindowLength == b.WindowLength).All(x => x),
                         "table round-trips the positions");

        using var seekInput = new MemoryStream(compressed);
        var stream = new DeflateRandomAccessStream(seekInput, 0, compressed.Length, original.Length, read);
        var random = new Random(11);
        bool allMatch = true;
        var slice = new byte[4096];
        TestReport.Timed("400 random seeks + 4 KB reads through the table's checkpoints", () =>
        {
            for (int i = 0; i < 400; i++)
            {
                long offset = i % 2 == 0 && i / 2 < read.Count ? Math.Min(read[i / 2].UncompressedOffset + random.Next(3), original.Length - slice.Length) : random.NextInt64(0, original.Length - slice.Length);
                stream.Position = offset;
                int filled = 0;
                while (filled < slice.Length) { int n = stream.Read(slice, filled, slice.Length - filled); if (n <= 0) break; filled += n; }
                if (!slice.AsSpan(0, filled).SequenceEqual(original.AsSpan((int)offset, filled))) { allMatch = false; Console.WriteLine($"   mismatch at {offset}"); }
            }
        });
        TestReport.Check(allMatch, "every random read through the compact table matches the original");
    }
}
