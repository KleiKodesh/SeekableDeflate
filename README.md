# SeekableDeflate

Random access into deflate-compressed data without keeping a decompressed copy — for .NET,
fully managed (net48, native AOT, WASM).

.NET's `DeflateStream` (zlib-ng) can only read forward from the start: it exposes no
`inflatePrime` / `inflateSetDictionary` / block-boundary stop, so it cannot resume mid-stream.
SeekableDeflate is its own RFC 1951 inflater that can:

- capture a **checkpoint** at a deflate block boundary — compressed byte + leftover bits, output
  offset, and the last 32 KB of output (zlib's `zran.c` technique);
- **resume** from any checkpoint later;
- expose the whole thing as a **seekable read-only `Stream`** (`DeflateRandomAccessStream`):
  a read at any offset resumes from the nearest earlier checkpoint and skips at most one span.

Plus `ZipEntryLocator`, which finds a zip entry's raw data offset / method / sizes (Zip64 aware)
straight from the central directory, so a zip member can be made seekable in place.

| Type | Role |
|---|---|
| `DeflateCheckpointInflater` | inflater; `BlockBoundaryReached` event → `CaptureCheckpoint()`; `ResumeFrom(checkpoint)` |
| `DeflateCheckpoint` | resumable state (position, bits, output offset, 32 KB window) |
| `DeflateRandomAccessStream` | seekable stream over a deflate region + a checkpoint list |
| `ZipEntryLocator`, `ZipEntryLocation`, `ZipStoredEntryStream` | zip entries in place |

Typical cost: one checkpoint per ~1 MB of output = ~32 KB each (~3 % of the data); a random 64 KB
read ≈ 2 ms. Used by [WordDocumentRandomAccess](https://github.com/KleiKodesh/WordDocumentRandomAccess)
to open huge .docx files instantly.

```
cd SeekableDeflate.Tests
dotnet run -c Release -- --megabytes 32
```

Consumed by source: put this repo next to the consumer and add a `ProjectReference`
(no NuGet package).
