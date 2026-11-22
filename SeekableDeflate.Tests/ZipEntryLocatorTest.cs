using System.IO.Compression;
using System.Text;
using SeekableDeflate;

namespace SeekableDeflate.Tests;

/// <summary>
/// <see cref="ZipEntryLocator"/> over the zip shapes a package can take: many entries, entries written with data descriptors
/// (sizes only in the central directory), a repeated name, a central directory whose recorded size is short, Zip64 extra
/// fields, and a malformed Zip64 extra field (must fail as bad data or read what is there, never with an argument error).
/// </summary>
public static class ZipEntryLocatorTest
{
    public static void Run()
    {
        TestReport.Step("Zip entry locator: central directory in one read");

        // many entries, written by ZipArchive (seekable: sizes in the local headers)
        byte[] many = MakeArchive(seekable: true, count: 1100);
        using (var zip = new MemoryStream(many))
        {
            bool all = true;
            foreach (int i in new[] { 0, 1, 549, 1099 })
                all &= ReadEntry(zip, $"part{i}.xml") == Content(i);
            TestReport.Check(all, "1,100 entries: the first, middle and last are located and read back");
            TestReport.Check(ZipEntryLocator.Locate(zip, "PART7.XML")?.Name == "part7.xml", "lookup ignores case");
            TestReport.Check(ZipEntryLocator.Locate(zip, "missing.xml") is null, "a missing name gives null");
            TestReport.Check(ZipEntryLocator.EnumerateEntries(zip).Count() == 1100, "enumeration lists every entry");
        }

        // data descriptors: a non-seekable output makes ZipArchive write the sizes after the data
        byte[] described = MakeArchive(seekable: false, count: 20);
        using (var zip = new MemoryStream(described))
        {
            bool hasDescriptor = (BitConverter.ToUInt16(described, 6) & 8) != 0;
            TestReport.Check(hasDescriptor && ReadEntry(zip, "part13.xml") == Content(13), "entries with data descriptors: sizes from the central directory");
        }

        // a repeated name: the first entry in the central directory, as before
        byte[] repeated = Build([("a.xml", "first"), ("b.xml", "other"), ("a.xml", "second")]);
        using (var zip = new MemoryStream(repeated))
            TestReport.Check(ReadEntry(zip, "a.xml") == "first", "a repeated name: the first one");

        // the end record's directory size is short: the directory is read further as its entries need
        byte[] shortSize = Build([("a.xml", "alpha"), ("b.xml", "beta"), ("c.xml", "gamma")], recordedSizeOverride: 10);
        using (var zip = new MemoryStream(shortSize))
            TestReport.Check(ReadEntry(zip, "c.xml") == "gamma", "a short recorded directory size: the last entry is still found");

        // Zip64: sizes and offset as 0xFFFFFFFF placeholders, the real values in the 0x0001 extra field
        byte[] zip64 = Build([("a.xml", "alpha"), ("z.xml", "zulu")], zip64: true);
        using (var zip = new MemoryStream(zip64))
            TestReport.Check(ReadEntry(zip, "z.xml") == "zulu", "Zip64 extra field: sizes and local header offset from it");

        // a Zip64 extra field that claims more bytes than the extra holds: bad data, never an argument error
        byte[] broken = Build([("a.xml", "alpha")], zip64: true, truncateZip64Extra: true);
        string outcome;
        using (var zip = new MemoryStream(broken))
        {
            try { ZipEntryLocator.Locate(zip, "a.xml"); outcome = "read"; }
            catch (InvalidDataException) { outcome = "bad data"; }
            catch (EndOfStreamException) { outcome = "bad data"; }
            catch (Exception exception) { outcome = exception.GetType().Name; }
        }
        TestReport.Check(outcome is "read" or "bad data", $"a truncated Zip64 extra field fails as bad data ({outcome})");
    }

    private static string Content(int i) => $"<p>entry {i} " + new string((char)('a' + i % 26), 50 + i % 200) + "</p>";

    private static string ReadEntry(Stream zip, string name)
    {
        ZipEntryLocation? entry = ZipEntryLocator.Locate(zip, name);
        if (entry is null) return "<missing>";
        zip.Position = entry.DataOffset;
        var data = new byte[entry.CompressedSize];
        zip.ReadExactly(data);
        if (entry.CompressionMethod == 0) return Encoding.UTF8.GetString(data);
        using var inflate = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        using var text = new StreamReader(inflate, Encoding.UTF8);
        return text.ReadToEnd();
    }

    private static byte[] MakeArchive(bool seekable, int count)
    {
        var buffer = new MemoryStream();
        Stream target = seekable ? buffer : new ForwardOnlyStream(buffer);
        using (var archive = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
            for (int i = 0; i < count; i++)
            {
                using var writer = new StreamWriter(archive.CreateEntry($"part{i}.xml").Open(), new UTF8Encoding(false));
                writer.Write(Content(i));
            }
        return buffer.ToArray();
    }

    /// <summary>A stored-entries zip written by hand (to make shapes ZipArchive does not write).</summary>
    private static byte[] Build((string name, string text)[] entries, long recordedSizeOverride = -1, bool zip64 = false, bool truncateZip64Extra = false)
    {
        var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        var offsets = new List<long>();
        foreach ((string name, string text) in entries)
        {
            offsets.Add(output.Position);
            byte[] nameBytes = Encoding.UTF8.GetBytes(name), data = Encoding.UTF8.GetBytes(text);
            writer.Write(0x04034b50u); writer.Write((ushort)20); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write(0u); writer.Write(Crc(data)); writer.Write((uint)data.Length); writer.Write((uint)data.Length);
            writer.Write((ushort)nameBytes.Length); writer.Write((ushort)0);
            writer.Write(nameBytes); writer.Write(data);
        }
        long directoryStart = output.Position;
        for (int i = 0; i < entries.Length; i++)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(entries[i].name), data = Encoding.UTF8.GetBytes(entries[i].text);
            byte[] extra = [];
            if (zip64)
            {
                var extraStream = new MemoryStream();
                var extraWriter = new BinaryWriter(extraStream);
                extraWriter.Write((ushort)1); extraWriter.Write((ushort)24);
                extraWriter.Write((long)data.Length); extraWriter.Write((long)data.Length);
                if (!truncateZip64Extra) extraWriter.Write(offsets[i]);     // truncated: the field claims 24 bytes, 16 follow
                extra = extraStream.ToArray();
            }
            uint size = zip64 ? 0xFFFFFFFF : (uint)data.Length, offset = zip64 ? 0xFFFFFFFF : (uint)offsets[i];
            writer.Write(0x02014b50u); writer.Write((ushort)45); writer.Write((ushort)45); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write(0u); writer.Write(Crc(data)); writer.Write(size); writer.Write(size);
            writer.Write((ushort)nameBytes.Length); writer.Write((ushort)extra.Length); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u); writer.Write(offset);
            writer.Write(nameBytes); writer.Write(extra);
        }
        long directorySize = output.Position - directoryStart;
        writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write((ushort)entries.Length); writer.Write((ushort)entries.Length);
        writer.Write((uint)(recordedSizeOverride >= 0 ? recordedSizeOverride : directorySize)); writer.Write((uint)directoryStart);
        writer.Write((ushort)0);
        writer.Flush();
        return output.ToArray();
    }

    private static uint Crc(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
