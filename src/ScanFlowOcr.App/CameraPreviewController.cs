using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ScanFlowOcr.Contracts;
using ScanFlowOcr.Imaging;

namespace ScanFlowOcr.App;

internal readonly record struct CameraPreviewMetrics(
    long FramesReceived, long FramesDropped, long FramesDecoded);

/// <summary>
/// FlashCap MJPEG session → TurboJPEG BGRA decode → Avalonia preview bitmap.
/// Decode runs off the UI thread; all WriteableBitmap / Image.Source / UI callbacks
/// are marshaled to <see cref="Dispatcher.UIThread"/>. Drops frames when the previous
/// decode is still in flight. Ignores in-flight work after stop/dispose.
/// </summary>
internal sealed class CameraPreviewController : IFrameReceiver, IAsyncDisposable
{
    private readonly ImageAllocator _allocator = new(64L * 1024 * 1024);
    private readonly object _latestGate = new();
    private readonly Action<string> _log;
    private readonly Action<string> _setError;
    private readonly Action<WriteableBitmap?> _setPreview;
    private readonly Action _onStopped;

    private JpegDecoder? _decoder;
    private ICameraSession? _session;
    private int _decodeBusy;
    private int _disposed;
    /// <summary>1 while the session should accept frames; cleared on stop before dispose races.</summary>
    private int _acceptFrames;
    private int _previewEpoch;
    private byte[]? _latestBgra;
    private int _latestWidth;
    private int _latestHeight;
    private FrameStamp? _latestStamp;
    private WriteableBitmap? _bitmap;
    private long _framesReceived;
    private long _framesDropped;
    private long _framesDecoded;

    public CameraPreviewController(
        Action<string> log,
        Action<string> setError,
        Action<WriteableBitmap?> setPreview,
        Action onStopped)
    {
        _log = log;
        _setError = setError;
        _setPreview = setPreview;
        _onStopped = onStopped;
    }

    public bool IsRunning => _session is { State: SourceState.Running or SourceState.Starting };

    public CameraPreviewMetrics GetMetrics() => new(
        Interlocked.Read(ref _framesReceived),
        Interlocked.Read(ref _framesDropped),
        Interlocked.Read(ref _framesDecoded));

    public bool TryGetLatestBgra(out byte[] bgra, out int width, out int height, out FrameStamp stamp)
    {
        lock (_latestGate)
        {
            if (_latestBgra is null || _latestStamp is null || _latestWidth <= 0 || _latestHeight <= 0)
            {
                bgra = [];
                width = height = 0;
                stamp = default;
                return false;
            }

            bgra = (byte[])_latestBgra.Clone();
            width = _latestWidth;
            height = _latestHeight;
            stamp = _latestStamp.Value;
            return true;
        }
    }

    public async Task StartAsync(ICameraSession session, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (!TurboJpegNative.TryResolveLibraryPath(out string? libraryPath, out string error))
        {
            PostUi(() => _setError(error));
            await session.DisposeAsync().ConfigureAwait(false);
            throw new FileNotFoundException(error);
        }

        PostUi(() => _setError(""));
        _decoder?.Dispose();
        _decoder = new JpegDecoder(libraryPath);
        Interlocked.Exchange(ref _framesReceived, 0);
        Interlocked.Exchange(ref _framesDropped, 0);
        Interlocked.Exchange(ref _framesDecoded, 0);
        PostUi(() => _log($"TurboJPEG loaded: {libraryPath}"));

        // Take ownership of the session for the lifetime of this controller.
        _session = session;
        Interlocked.Increment(ref _previewEpoch);
        Volatile.Write(ref _acceptFrames, 1);
        try
        {
            var epoch = Guid.NewGuid();
            await session.StartAsync(epoch, this, cancellationToken).ConfigureAwait(false);
            string name = session.Device.DisplayName;
            int w = session.NegotiatedMode.Width;
            int h = session.NegotiatedMode.Height;
            PostUi(() => _log($"Camera started: {name} {w}×{h}"));
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        // Stop accepting frames first so in-flight decode/UI posts become no-ops.
        Volatile.Write(ref _acceptFrames, 0);
        Interlocked.Increment(ref _previewEpoch);

        ICameraSession? session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            await WaitForDecodeIdleAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
            await WaitForDecodeIdleAsync().ConfigureAwait(false);
            PostUi(() =>
            {
                ClearPreviewBitmap();
                _onStopped();
            });
        }
    }

    public void OnFrame(in CapturedFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Volatile.Read(ref _acceptFrames) == 0) return;
        Interlocked.Increment(ref _framesReceived);
        JpegDecoder? decoder = _decoder;
        if (decoder is null) return;
        if (frame.Layout.Encoding != FrameEncoding.Jpeg)
        {
            Interlocked.Increment(ref _framesDropped);
            return;
        }
        if (Interlocked.CompareExchange(ref _decodeBusy, 1, 0) != 0)
        {
            Interlocked.Increment(ref _framesDropped);
            return;
        }

        byte[] jpeg = frame.Buffer.ToArray();
        var stamp = frame.Stamp;
        var layout = frame.Layout;
        int epoch = Volatile.Read(ref _previewEpoch);

        _ = Task.Run(() =>
        {
            try
            {
                if (Volatile.Read(ref _acceptFrames) == 0 ||
                    Volatile.Read(ref _disposed) != 0 ||
                    Volatile.Read(ref _previewEpoch) != epoch)
                {
                    return;
                }

                var input = new ImageInput(stamp, layout, jpeg, ImageTransform.Identity);
                using ImageLease bgraLease = decoder.DecodeBgra(input, _allocator);
                byte[] bgra = bgraLease.WritableBuffer.ToArray();
                int w = layout.Width;
                int h = layout.Height;
                int stride = w * 4;
                Interlocked.Increment(ref _framesDecoded);

                if (Volatile.Read(ref _acceptFrames) == 0 ||
                    Volatile.Read(ref _disposed) != 0 ||
                    Volatile.Read(ref _previewEpoch) != epoch)
                {
                    return;
                }

                lock (_latestGate)
                {
                    _latestBgra = bgra;
                    _latestWidth = w;
                    _latestHeight = h;
                    _latestStamp = stamp;
                }

                // Bitmap create/lock/Source assign must run on the UI thread.
                PostUi(() =>
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    if (Volatile.Read(ref _acceptFrames) == 0) return;
                    if (Volatile.Read(ref _previewEpoch) != epoch) return;
                    try
                    {
                        if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
                        {
                            _bitmap?.Dispose();
                            _bitmap = new WriteableBitmap(
                                new PixelSize(w, h),
                                new Vector(96, 96),
                                PixelFormats.Bgra8888,
                                AlphaFormat.Opaque);
                        }

                        using (var fb = _bitmap.Lock())
                        {
                            for (int y = 0; y < h; y++)
                            {
                                Marshal.Copy(
                                    bgra,
                                    y * stride,
                                    IntPtr.Add(fb.Address, y * fb.RowBytes),
                                    stride);
                            }
                        }

                        _setPreview(_bitmap);
                    }
                    catch (Exception ex)
                    {
                        _setError($"Preview update failed: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _framesDropped);
                PostUi(() => _setError($"TurboJPEG decode failed: {ex.Message}"));
            }
            finally
            {
                Interlocked.Exchange(ref _decodeBusy, 0);
            }
        });
    }

    public void OnFault(CaptureFault fault)
    {
        PostUi(() =>
        {
            _setError($"Camera fault: {fault.Code} — {fault.Message}");
            _log($"Camera fault: {fault.Code} {fault.Message} (reconnect={fault.CanReconnect})");
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _acceptFrames, 0);
        Interlocked.Increment(ref _previewEpoch);
        await StopAsync().ConfigureAwait(false);
        await WaitForDecodeIdleAsync().ConfigureAwait(false);
        _decoder?.Dispose();
        _decoder = null;
        PostUi(() =>
        {
            ClearPreviewBitmap();
        });
    }

    private void ClearPreviewBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _setPreview(null);
    }

    private static void PostUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action, DispatcherPriority.Render);
    }

    private async Task WaitForDecodeIdleAsync()
    {
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _decodeBusy) != 0)
        {
            if (sw.ElapsedMilliseconds > 5000)
                break;
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
