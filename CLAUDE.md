# SeekableDeflate

Managed, seekable deflate (zran-style checkpoints) + zip-entry location. Extracted 2026-10-28 from
WordDocumentRandomAccess (origin: that repo's `WordDocumentRandomAccess/Deflate` + `/Zip` at
commit 58e4dfc), which consumes it as a sibling-folder `ProjectReference`. No NuGet.

Rules (same as the consumer's CLAUDE.md):
- Fully managed: no P/Invoke, no reflection, `IsAotCompatible` on; net48 + net10.0 must both build.
- **Speed: fix the implementation, never hide it** — no threading / pipelines / parallel loops.
  Next work: rebuild the hot path on zlib's `inflate_fast` design (combined length/distance table
  entries, fewer bounds checks). Baseline (91.6 MB XML): ~225–305 MB/s vs .NET zlib ~830 MB/s.
- Verify format facts in RFC 1951 / zlib source, never from memory.
- Byte-exact correctness against .NET's `DeflateStream` is checked on every change
  (`SeekableDeflate.Tests`, must print ALL PASSED). A change that breaks the consumer's harness
  (`WordDocumentRandomAccess.Tests -- --real …`) is not done.
- QA before each commit; a failed build stops the commit; commit as you go;
  author KleiKodesh, no Co-Authored-By trailer.
- Rejected, don't re-propose: ZLibDotNet 0.1.1 (mis-decodes real data), .NET DeflateStream (no resume API).
