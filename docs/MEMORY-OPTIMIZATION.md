# 单进程内存优化

2026-09-20：保留进程内 OCR。

- 启动连续扫描前释放闲置静态图 OCR reader，避免两套模型同时常驻。
- ImageAllocator 使用实际大小的非托管缓冲，并在会话内按精确尺寸复用；`LiveBytes` 统计使用中数据，`CommittedBytes` 统计使用中加空闲块。总容量受 `RetainedByteLimit` 限制，停止/重配置时 `TrimExcess` 释放空闲块，不再由共享 ArrayPool 保留像素桶。
- JPEG 连续扫描和独立相机预览都支持 TurboJPEG 原生 1/2、1/4、1/8 缩放解码。新配置默认预览上限 1280×720；OCR 路径仍按原图解码。独立预览点击 OCR 时按需重新解码原始帧，不使用低清预览位图。
- BGR ROI 复用原缓冲 offset/stride，在识别结束前保留源 lease，不复制裁剪像素。
- 日志正文最多保留 64K 字符。
- viewport 缩放以左上角为变换原点，用光标位置修正平移量，鼠标下的图像点在缩放前后保持不动。

构建和 smoke 已通过，覆盖缓冲引用计数、ROI 共享像素和坐标、JPEG 缩放及释放、JPEG/BGRA OCR 格式转换。

尚待真实相机长跑、真实模型带 stride ROI 验证，以及相同模型/输入条件下的 Private Bytes、工作集、GPU 内存前后对照。没有实测节省 MB 数值。
