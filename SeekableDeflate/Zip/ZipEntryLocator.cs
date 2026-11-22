using System.Text;

namespace SeekableDeflate;

/// <summary>
/// Reads a zip's central directory (Zip64 aware) and reports where an entry's raw data begins,
/// so the deflate layer can work on the original file without ZipArchive and without a copy.
/// Format reference: PKWARE APPNOTE.TXT §4.3 (local header 0x04034b50, central directory
/// 0x02014b50, end of central directory 0x06054b50, Zip64 records 0x06064b50 / 0x07064b50).
/// </summary>
public static class ZipEntryLocator
{
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndLocatorSignature = 0x07064b50;
    private const uint Zip64EndRecordSignature = 0x06064b50;
    private const uint CentralFileHeaderSignature = 0x02014b50;
    private const uint LocalFileHeaderSignature = 0x04034b50;
    private const int MaxCommentLength = 0xFFFF;

    /// <summary>
    /// The entry named <paramref name="entryName"/> (case-insensitive), or null. The central directory is read in one
    /// block and only the match's local header is read: a package with a thousand parts is one read, not a seek per part.
    /// </summary>
    public static ZipEntryLocation? Locate(Stream zip, string entryName)
    {
        foreach (CentralDirectoryEntry entry in ReadCentralDirectory(zip))
            if (string.Equals(entry.Name, entryName, StringComparison.OrdinalIgnoreCase)) return LocationOf(zip, entry);
        return null;
    }

    public static IEnumerable<ZipEntryLocation> EnumerateEntries(Stream zip)
    {
        foreach (CentralDirectoryEntry entry in ReadCentralDirectory(zip)) yield return LocationOf(zip, entry);
    }

    private readonly record struct CentralDirectoryEntry(string Name, ushort Method, uint Crc, long CompressedSize, long UncompressedSize, long LocalHeaderOffset);

    private static ZipEntryLocation LocationOf(Stream zip, CentralDirectoryEntry entry)
    {
        var reader = new BinaryReader(zip, Encoding.UTF8, leaveOpen: true);
        long dataOffset = DataOffsetFromLocalHeader(zip, reader, entry.LocalHeaderOffset);
        return new ZipEntryLocation(entry.Name, dataOffset, entry.CompressedSize, entry.UncompressedSize, entry.Method, entry.Crc);
    }

    /// <summary>The central directory entries in order, parsed from one read of the directory (APPNOTE 4.3.12), as they are asked for.</summary>
    private static IEnumerable<CentralDirectoryEntry> ReadCentralDirectory(Stream zip)
    {
        (long centralDirectoryOffset, long entryCount, long recordedSize) = FindCentralDirectory(zip);
        if (centralDirectoryOffset < 0 || centralDirectoryOffset > zip.Length) throw new InvalidDataException("Zip: bad central directory entry.");
        // read the recorded size in one go; a directory whose recorded size is short is read further as its entries need
        long available = zip.Length - centralDirectoryOffset;
        var directory = new byte[(int)Math.Min(Math.Min(Math.Max(recordedSize, 46), available), int.MaxValue)];
        zip.Position = centralDirectoryOffset;
        ReadExactly(zip, directory);
        int loaded = directory.Length;
        bool Ensure(int end)
        {
            if (end <= loaded) return true;
            if (end > available) return false;
            int size = (int)Math.Min(Math.Max((long)end, (long)loaded * 2), Math.Min(available, int.MaxValue));
            Array.Resize(ref directory, size);
            zip.Position = centralDirectoryOffset + loaded;
            ReadExactly(zip, directory, loaded, size - loaded);
            loaded = size;
            return true;
        }
        int position = 0;
        for (long i = 0; i < entryCount; i++)
        {
            if (!Ensure(position + 46) || BitConverter.ToUInt32(directory, position) != CentralFileHeaderSignature)
                throw new InvalidDataException("Zip: bad central directory entry.");
            ushort method = BitConverter.ToUInt16(directory, position + 10);
            uint crc = BitConverter.ToUInt32(directory, position + 16);
            long compressedSize = BitConverter.ToUInt32(directory, position + 20);
            long uncompressedSize = BitConverter.ToUInt32(directory, position + 24);
            int nameLength = BitConverter.ToUInt16(directory, position + 28);
            int extraLength = BitConverter.ToUInt16(directory, position + 30);
            int commentLength = BitConverter.ToUInt16(directory, position + 32);
            long localHeaderOffset = BitConverter.ToUInt32(directory, position + 42);
            int nameStart = position + 46;
            if (!Ensure(nameStart + nameLength + extraLength)) throw new EndOfStreamException();
            string name = Encoding.UTF8.GetString(directory, nameStart, nameLength);
            var extra = new byte[extraLength];
            Buffer.BlockCopy(directory, nameStart + nameLength, extra, 0, extraLength);
            ReadZip64Extra(extra, ref uncompressedSize, ref compressedSize, ref localHeaderOffset);
            position = nameStart + nameLength + extraLength + commentLength;
            yield return new CentralDirectoryEntry(name, method, crc, compressedSize, uncompressedSize, localHeaderOffset);
        }
    }

    private static long DataOffsetFromLocalHeader(Stream zip, BinaryReader reader, long localHeaderOffset)
    {
        zip.Position = localHeaderOffset;
        if (reader.ReadUInt32() != LocalFileHeaderSignature) throw new InvalidDataException("Zip: bad local file header.");
        zip.Position += 22;                                        // up to the name length field
        int nameLength = reader.ReadUInt16();
        int extraLength = reader.ReadUInt16();
        return localHeaderOffset + 30 + nameLength + extraLength;
    }

    /// <summary>Zip64 extra field (0x0001): the 0xFFFFFFFF placeholders are replaced in field order.</summary>
    private static void ReadZip64Extra(byte[] extra, ref long uncompressedSize, ref long compressedSize, ref long localHeaderOffset)
    {
        int position = 0;
        while (position + 4 <= extra.Length)
        {
            ushort id = BitConverter.ToUInt16(extra, position);
            ushort size = BitConverter.ToUInt16(extra, position + 2);
            position += 4;
            if (id == 0x0001)
            {
                int field = position;
                if (uncompressedSize == 0xFFFFFFFF && field + 8 <= position + size) { uncompressedSize = BitConverter.ToInt64(extra, field); field += 8; }
                if (compressedSize == 0xFFFFFFFF && field + 8 <= position + size) { compressedSize = BitConverter.ToInt64(extra, field); field += 8; }
                if (localHeaderOffset == 0xFFFFFFFF && field + 8 <= position + size) localHeaderOffset = BitConverter.ToInt64(extra, field);
                return;
            }
            position += size;
        }
    }

    private static (long offset, long entryCount, long size) FindCentralDirectory(Stream zip)
    {
        long searchStart = Math.Max(0, zip.Length - MaxCommentLength - 22);
        var tail = new byte[zip.Length - searchStart];
        zip.Position = searchStart;
        ReadExactly(zip, tail);

        for (int i = tail.Length - 22; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(tail, i) != EndOfCentralDirectorySignature) continue;
            long entryCount = BitConverter.ToUInt16(tail, i + 10);
            long size = BitConverter.ToUInt32(tail, i + 12);
            long offset = BitConverter.ToUInt32(tail, i + 16);
            if (entryCount == 0xFFFF || offset == 0xFFFFFFFF || size == 0xFFFFFFFF)
            {
                long endRecordPosition = searchStart + i;
                (offset, entryCount, size) = ReadZip64EndRecord(zip, endRecordPosition) ?? (offset, entryCount, size);
            }
            return (offset, entryCount, size);
        }
        throw new InvalidDataException("Zip: end of central directory not found.");
    }

    private static (long offset, long entryCount, long size)? ReadZip64EndRecord(Stream zip, long endRecordPosition)
    {
        long locatorPosition = endRecordPosition - 20;
        if (locatorPosition < 0) return null;
        var reader = new BinaryReader(zip, Encoding.UTF8, leaveOpen: true);
        zip.Position = locatorPosition;
        if (reader.ReadUInt32() != Zip64EndLocatorSignature) return null;
        zip.Position += 4;                                         // disk with the zip64 end record
        long zip64EndPosition = reader.ReadInt64();
        zip.Position = zip64EndPosition;
        if (reader.ReadUInt32() != Zip64EndRecordSignature) return null;
        zip.Position += 8 + 2 + 2 + 4 + 4 + 8;                     // size, versions, disks, entries on this disk
        long entryCount = reader.ReadInt64();
        long size = reader.ReadInt64();
        long offset = reader.ReadInt64();
        return (offset, entryCount, size);
    }

    private static void ReadExactly(Stream stream, byte[] buffer) => ReadExactly(stream, buffer, 0, buffer.Length);

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        int filled = 0;
        while (filled < count)
        {
            int read = stream.Read(buffer, offset + filled, count - filled);
            if (read <= 0) throw new EndOfStreamException();
            filled += read;
        }
    }
}
