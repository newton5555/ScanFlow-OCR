# ScanFlowOcr.Ocr.SimdPaddle

Product OCR adapter over Sdcb.SimdPaddleOCR 1.4.2 and the
Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny / ChineseV6Small and
Sdcb.SimdPaddleOCR.Models.TextLineOrientation 1.0.0 model packages.

The adapter runs the managed/CPU-SIMD PP-OCRv6 implementation in-process. It is
not a separate PaddleOCR Python service or a claim that the application embeds
the official Paddle native runtime.

Upstream source is Apache-2.0. PP-OCRv6 models retain their upstream notices.
This project does not re-license those components. Publish layouts must include
the upstream LICENSE and THIRD-PARTY-NOTICES.

ScanFlow does not encrypt or embed-protect these assemblies.
