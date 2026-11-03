# SeekableDeflate

Managed, seekable deflate (zran-style checkpoints) + zip-entry location. Extracted 2026-10-28 from
FastDocView (origin: that repo's `FastDocView/Deflate` + `/Zip` at
commit 58e4dfc), which consumes it as a sibling-folder `ProjectReference`. No NuGet.

Rules (same as the consumer's CLAUDE.md):
- Fully managed: no P/Invoke, no reflection, `IsAotCompatible` on; net48 + net10.0 must both build.
- **Speed: fix the implementation, never hide it** — no threading / pipelines / parallel loops.
  The hot loop (2026-11-03) follows zlib's `inflate_fast` / libdeflate: bit buffer in locals, 8-byte
  refills, table entries that carry literal / base + extra bits, one linear output buffer with the
  32 KB history in front, 8-byte word match copies (`Unsafe`), `AggressiveOptimization` on the loop.
  Real document.xml, sequential from a checkpoint: ~350-450 MB/s at steady state — the same as the
  previous loop once the tiered JIT had promoted it; the gain is that it runs at that speed from the
  first call, where the old one spent its first seconds (tens of MB) at ~90 MB/s in tier-0 code
  (the "95 MB/s" measured in FastDocView NOTES was that). .NET zlib: ~770 MB/s. Next lever: feed
  the span after a checkpoint to .NET's native `DeflateStream` as a synthetic stream — a stored
  block holding the window, then the remaining compressed bytes re-packed by the checkpoint's
  leftover-bit shift (no resume API needed; 32 KB of output to skip). Not done: our inflater is
  within 2× and the read path stays one implementation.
- Verify format facts in RFC 1951 / zlib source, never from memory.
- Byte-exact correctness against .NET's `DeflateStream` is checked on every change
  (`SeekableDeflate.Tests`, must print ALL PASSED). A change that breaks the consumer's harness
  (`FastDocView.Tests -- --real …`) is not done.
- QA before each commit; a failed build stops the commit; commit as you go;
  author KleiKodesh, no Co-Authored-By trailer.
- Rejected, don't re-propose: ZLibDotNet 0.1.1 (mis-decodes real data), .NET DeflateStream (no resume API).
