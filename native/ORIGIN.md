# Official libjpeg-turbo binaries (3.2.0)

Extracted shared libraries only — do **not** commit the full `.exe` / `.deb` installers.

Loader expectations (`ScanFlowOcr.Imaging.TurboJpegNative`):

- `native/win-x64/turbojpeg.dll`
- `native/linux-x64/libturbojpeg.so`

## Windows x64 (Visual C++)

- Package: [libjpeg-turbo-3.2.0-vc-x64.exe](https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.2.0/libjpeg-turbo-3.2.0-vc-x64.exe)
- SHA256: `662761d8ba8dae04aec74023ebaeceb856c2b56b9b59cfd180759d26300dda42`
- Extracted file: `bin/turbojpeg.dll` → `win-x64/turbojpeg.dll`

## Linux amd64 (official deb)

- Package: [libjpeg-turbo-official_3.2.0_amd64.deb](https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.2.0/libjpeg-turbo-official_3.2.0_amd64.deb)
- SHA256: `21297da4a4eb34ebefc54afca5d8dd86c0fdd6a9dfe49b1b962c5d1eeeafd8ec`
- Extracted file: `opt/libjpeg-turbo/lib64/libturbojpeg.so.0.5.0` → `linux-x64/libturbojpeg.so`  
  (copied under the filename the managed loader resolves; soname in the ELF is still `libturbojpeg.so.0`)

Override path at runtime with env `SCANFLOW_OCR_TURBOJPEG_PATH`.

Upstream: https://github.com/libjpeg-turbo/libjpeg-turbo/releases/tag/3.2.0  
License: IJG + Modified (3-clause) BSD — see `../THIRD-PARTY-NOTICES.md`.
