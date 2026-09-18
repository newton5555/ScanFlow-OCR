# Linux V4L2 wiring

FlashCap's vendored `CaptureDevices` already selects `V4L2Devices` when
`NativeMethods.IsLinux()` is true. `FlashCapProvider` uses:

- Windows 7+: `MediaFoundationDevices` (MJPEG-only advertised)
- Linux: default `CaptureDevices()` → V4L2

No extra adapter layer is required beyond choosing the backend. Device nodes
are typically `/dev/video*`; the process needs permission to open them
(e.g. membership in the `video` group).
