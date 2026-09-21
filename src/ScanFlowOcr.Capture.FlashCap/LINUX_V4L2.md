# Linux V4L2 wiring

FlashCap's vendored `CaptureDevices` already selects `V4L2Devices` when
`NativeMethods.IsLinux()` is true. `FlashCapProvider` uses:

- Windows: `MediaFoundationDevices` first, then the configured FlashCap fallback set
- Linux: default `CaptureDevices()` → V4L2

The application currently keeps descriptors whose format is `JPEG`, `RGB24`, or
`RGB32`. V4L2 may enumerate additional formats such as YUYV, NV12, or PNG, but
those are not automatically accepted by the application. The accepted raw RGB
formats are normalized into the app's BGR/BGRA input paths; JPEG/MJPEG is decoded
through the native TurboJPEG path.

No extra adapter layer is required beyond choosing the backend. Device nodes
are typically `/dev/video*`; the process needs permission to open them
(e.g. membership in the `video` group). Device availability and format
negotiation still need to be verified on the target camera/driver combination.
