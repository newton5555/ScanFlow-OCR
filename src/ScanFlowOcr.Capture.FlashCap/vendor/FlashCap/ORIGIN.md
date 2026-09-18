# FlashCap source dependency

- Upstream: https://github.com/kekyo/FlashCap
- Pinned commit: `b0e1a183df99cb2675e7e5dc4f97e7d4466d1359`
- License: Apache-2.0, retained in LICENSE and source headers.
- Imported: FlashCap.Core and FlashCap source directories. No upstream build outputs.
- ScanFlow build entry points: `FlashCap.Core.Local.csproj`, `FlashCap.Local.csproj`, and this directory's `Directory.Build.props`. Original upstream project files are retained for comparison but not built by ScanFlow.
- Local build targets .NET 10, enables `FLASHCAP_MEDIAFOUNDATION`, and does not inherit ScanFlow application analyzers or implicit usings.
- Upstream C# sources are unmodified. ScanFlow selects the existing Media Foundation backend first on Windows in its own adapter, then falls back to FlashCap's default Windows backend set when no MJPEG descriptor is exposed. This avoids hiding cameras that only publish MJPEG through DirectShow/VfW while keeping MF preferred for the tested repeat-open memory behavior; it is not a fix to DirectShow itself.

Update deliberately: pin another commit, compare upstream source changes, preserve license, and rerun USB lifecycle tests before accepting it. See `doc/usb-memory-investigation.md` at the repository root.
