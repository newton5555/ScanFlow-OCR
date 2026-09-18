using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.App;

public partial class MainWindow
{
    private enum RoiDragMode { None, Create, Move, NorthWest, NorthEast, SouthWest, SouthEast }

    private bool _isEditingRoi;
    private bool _roiBusy;
    private bool _roiPreviousStartEnabled;
    private bool _roiPreviousDevicesEnabled;
    private bool _roiPreviousModesEnabled;
    private RoiDragMode _roiDragMode;
    private Rect _roiDraft;
    private Rect _roiDragOrigin;
    private Point _roiDragStart;

    private async void OnBeginRoiEdit(object? sender, RoutedEventArgs e) => await BeginRoiEditAsync();

    private async Task BeginRoiEditAsync()
    {
        if (_isEditingRoi || _roiBusy) return;
        if (_activeSession is not null)
        {
            Append("连续扫描运行中，请先停止扫描再编辑 ROI。");
            return;
        }

        if (PreviewImage.Source is null || _sourceWidth <= 0 || _sourceHeight <= 0)
        {
            Append("请先启动相机预览或显示一张图片，再框选 ROI。");
            return;
        }

        _roiBusy = true;
        BtnEditRoi.IsEnabled = false;
        try
        {
            StopViewportPan();
            _roiDraft = MakeSafeRect(
                Math.Clamp(_settings.RoiX / 100.0, 0, 1),
                Math.Clamp(_settings.RoiY / 100.0, 0, 1),
                Math.Clamp((_settings.RoiX + _settings.RoiWidth) / 100.0, 0, 1),
                Math.Clamp((_settings.RoiY + _settings.RoiHeight) / 100.0, 0, 1));

            _roiPreviousStartEnabled = StartScanButton.IsEnabled;
            _roiPreviousDevicesEnabled = CameraDeviceCombo.IsEnabled;
            _roiPreviousModesEnabled = CameraModeCombo.IsEnabled;
            StartScanButton.IsEnabled = false;
            CameraDeviceCombo.IsEnabled = false;
            CameraModeCombo.IsEnabled = false;
            BtnEditRoi.IsVisible = false;
            RoiEditorBar.IsVisible = true;
            ResultToastScrollerOcr.IsVisible = false;
            ViewportCanvasArea.Cursor = new Cursor(StandardCursorType.Cross);
            _isEditingRoi = true;
            UpdateRoiDraftText();
            RedrawOverlay();
            SetStatus("正在编辑 ROI：拖动空白处框选，拖动区域移动，拖动四角调整。");
            Append("正在编辑 ROI：拖动空白处框选，拖动区域移动，拖动四角调整。保存后重新启动扫描。");
        }
        catch (Exception ex)
        {
            Append("无法进入 ROI 编辑: " + ex.Message);
        }
        finally
        {
            _roiBusy = false;
            BtnEditRoi.IsEnabled = true;
            if (_isEditingRoi) UpdateRoiDraftText();
        }
    }

    private async void OnSaveRoi(object? sender, RoutedEventArgs e)
    {
        if (!_isEditingRoi || !IsRoiDraftValid() || _roiBusy) return;
        _roiBusy = true;
        BtnRoiSave.IsEnabled = false;
        try
        {
            var settings = _settings.Clone();
            settings.EnableRoi = true;
            settings.RoiX = _roiDraft.Left * 100.0;
            settings.RoiY = _roiDraft.Top * 100.0;
            settings.RoiWidth = _roiDraft.Width * 100.0;
            settings.RoiHeight = _roiDraft.Height * 100.0;
            if (!settings.Validate(out string? error))
            {
                Append(error ?? "ROI 配置无效。");
                return;
            }

            await _settingsManager.SaveAsync(settings).ConfigureAwait(true);
            _settings = settings;
            try { _coordinator.Configure(_settings.GetOutputRoutes()); } catch { /* preserve saved ROI */ }
            EndRoiEdit();
            SetStatus("ROI 已保存；下次启动扫描时生效。");
            Append("ROI 已保存；下次启动扫描时生效。");
        }
        catch (Exception ex)
        {
            Append($"ROI 保存失败: {ex.Message}");
        }
        finally
        {
            _roiBusy = false;
            if (_isEditingRoi) BtnRoiSave.IsEnabled = IsRoiDraftValid();
        }
    }

    private void OnCancelRoi(object? sender, RoutedEventArgs e)
    {
        if (!_roiBusy) EndRoiEdit();
    }

    private void OnFullRoi(object? sender, RoutedEventArgs e)
    {
        if (!_isEditingRoi || _roiBusy) return;
        _roiDraft = new Rect(0, 0, 1, 1);
        UpdateRoiDraftText();
        RedrawOverlay();
    }

    private void EndRoiEdit()
    {
        if (!_isEditingRoi) return;
        _roiDragMode = RoiDragMode.None;
        _isEditingRoi = false;
        _roiBusy = false;
        RoiEditorBar.IsVisible = false;
        BtnEditRoi.IsVisible = true;
        StartScanButton.IsEnabled = _roiPreviousStartEnabled;
        CameraDeviceCombo.IsEnabled = _roiPreviousDevicesEnabled;
        CameraModeCombo.IsEnabled = _roiPreviousModesEnabled;
        ViewportCanvasArea.Cursor = new Cursor(StandardCursorType.Arrow);
        SyncFrameAnnotations();
    }

    private bool IsRoiDraftValid() =>
        _sourceWidth > 0 && _sourceHeight > 0 &&
        _roiDraft.Width >= 1.0 / _sourceWidth &&
        _roiDraft.Height >= 1.0 / _sourceHeight;

    private void UpdateRoiDraftText()
    {
        TxtRoiDraftCoordinates.Text = string.Create(CultureInfo.InvariantCulture,
            $"X1 {_roiDraft.Left * 100.0:0.0}%   Y1 {_roiDraft.Top * 100.0:0.0}%   X2 {_roiDraft.Right * 100.0:0.0}%   Y2 {_roiDraft.Bottom * 100.0:0.0}%");
        BtnRoiSave.IsEnabled = IsRoiDraftValid() && !_roiBusy;
    }

    private bool TryGetRoiGeometry(out double imageX, out double imageY, out double imageW, out double imageH)
    {
        imageX = imageY = imageW = imageH = 0;
        double canvasW = OverlayCanvas.Bounds.Width;
        double canvasH = OverlayCanvas.Bounds.Height;
        if (_sourceWidth <= 0 || _sourceHeight <= 0 || canvasW <= 0 || canvasH <= 0) return false;
        double scale = Math.Min(canvasW / _sourceWidth, canvasH / _sourceHeight);
        imageW = _sourceWidth * scale;
        imageH = _sourceHeight * scale;
        imageX = (canvasW - imageW) / 2.0;
        imageY = (canvasH - imageH) / 2.0;
        return imageW > 0 && imageH > 0;
    }

    private bool TryGetNormalizedPointer(PointerEventArgs e, bool allowOutside, out Point point)
    {
        point = default;
        if (!TryGetRoiGeometry(out var imageX, out var imageY, out var imageW, out var imageH)) return false;
        Point local = e.GetPosition(OverlayCanvas);
        double nx = (local.X - imageX) / imageW;
        double ny = (local.Y - imageY) / imageH;
        if (!allowOutside && (nx < 0 || nx > 1 || ny < 0 || ny > 1)) return false;
        point = new Point(Math.Clamp(nx, 0, 1), Math.Clamp(ny, 0, 1));
        return true;
    }

    private void HandleRoiMouseDown(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(OverlayCanvas).Properties;
        if (!props.IsLeftButtonPressed || !TryGetNormalizedPointer(e, false, out var point)) return;
        if (!TryGetRoiGeometry(out _, out _, out var width, out var height)) return;

        _roiDragStart = point;
        _roiDragOrigin = _roiDraft;
        double hitX = 10 / Math.Max(0.2, _zoomFactor) / width;
        double hitY = 10 / Math.Max(0.2, _zoomFactor) / height;
        bool Near(double x, double y) => Math.Abs(point.X - x) <= hitX && Math.Abs(point.Y - y) <= hitY;

        _roiDragMode = Near(_roiDraft.Left, _roiDraft.Top) ? RoiDragMode.NorthWest :
            Near(_roiDraft.Right, _roiDraft.Top) ? RoiDragMode.NorthEast :
            Near(_roiDraft.Left, _roiDraft.Bottom) ? RoiDragMode.SouthWest :
            Near(_roiDraft.Right, _roiDraft.Bottom) ? RoiDragMode.SouthEast :
            _roiDraft.Contains(point) ? RoiDragMode.Move : RoiDragMode.Create;

        e.Pointer.Capture(ViewportCanvasArea);
        e.Handled = true;
    }

    private void HandleRoiMouseMove(PointerEventArgs e)
    {
        if (_roiDragMode == RoiDragMode.None || !TryGetNormalizedPointer(e, true, out var point)) return;
        UpdateRoiDraftForPoint(point);
        e.Handled = true;
    }

    private void UpdateRoiDraftForPoint(Point point)
    {
        double minW = 1.0 / Math.Max(1, _sourceWidth);
        double minH = 1.0 / Math.Max(1, _sourceHeight);
        Rect r = _roiDragOrigin;
        switch (_roiDragMode)
        {
            case RoiDragMode.Create:
                r = MakeSafeRect(
                    Math.Min(_roiDragStart.X, point.X),
                    Math.Min(_roiDragStart.Y, point.Y),
                    Math.Max(_roiDragStart.X, point.X),
                    Math.Max(_roiDragStart.Y, point.Y));
                break;
            case RoiDragMode.Move:
                double maxLeft = Math.Max(0, 1.0 - _roiDragOrigin.Width);
                double maxTop = Math.Max(0, 1.0 - _roiDragOrigin.Height);
                double newLeft = Math.Clamp(_roiDragOrigin.X + (point.X - _roiDragStart.X), 0, maxLeft);
                double newTop = Math.Clamp(_roiDragOrigin.Y + (point.Y - _roiDragStart.Y), 0, maxTop);
                r = new Rect(newLeft, newTop, _roiDragOrigin.Width, _roiDragOrigin.Height);
                break;
            case RoiDragMode.NorthWest:
                double nwX = Math.Clamp(point.X, 0, Math.Max(0, r.Right - minW));
                double nwY = Math.Clamp(point.Y, 0, Math.Max(0, r.Bottom - minH));
                r = MakeSafeRect(nwX, nwY, r.Right, r.Bottom);
                break;
            case RoiDragMode.NorthEast:
                double neY = Math.Clamp(point.Y, 0, Math.Max(0, r.Bottom - minH));
                double neX = Math.Clamp(point.X, Math.Min(1, r.Left + minW), 1);
                r = MakeSafeRect(r.Left, neY, neX, r.Bottom);
                break;
            case RoiDragMode.SouthWest:
                double swX = Math.Clamp(point.X, 0, Math.Max(0, r.Right - minW));
                double swY = Math.Clamp(point.Y, Math.Min(1, r.Top + minH), 1);
                r = MakeSafeRect(swX, r.Top, r.Right, swY);
                break;
            case RoiDragMode.SouthEast:
                double seX = Math.Clamp(point.X, Math.Min(1, r.Left + minW), 1);
                double seY = Math.Clamp(point.Y, Math.Min(1, r.Top + minH), 1);
                r = MakeSafeRect(r.Left, r.Top, seX, seY);
                break;
        }

        _roiDraft = r;
        UpdateRoiDraftText();
        RedrawOverlay();
    }

    private static Rect MakeSafeRect(double p1x, double p1y, double p2x, double p2y)
    {
        double left = Math.Min(p1x, p2x);
        double top = Math.Min(p1y, p2y);
        double right = Math.Max(p1x, p2x);
        double bottom = Math.Max(p1y, p2y);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private void HandleRoiMouseUp(PointerReleasedEventArgs e)
    {
        if (_roiDragMode == RoiDragMode.None || e.InitialPressMouseButton != MouseButton.Left) return;
        if (!IsRoiDraftValid()) _roiDraft = _roiDragOrigin;
        _roiDragMode = RoiDragMode.None;
        e.Pointer.Capture(null);
        UpdateRoiDraftText();
        RedrawOverlay();
        e.Handled = true;
    }

    private void OnRoiLostMouseCapture(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_roiDragMode == RoiDragMode.None) return;
        _roiDragMode = RoiDragMode.None;
        if (!IsRoiDraftValid()) _roiDraft = _roiDragOrigin;
        UpdateRoiDraftText();
        RedrawOverlay();
    }

    private void DrawRoiEditorOverlay()
    {
        if (!TryGetRoiGeometry(out var x, out var y, out var w, out var h)) return;
        IBrush shade = new SolidColorBrush(Color.FromArgb(72, 0, 0, 0));
        IBrush accent = ResolveBrush("BrandBrush", new SolidColorBrush(Color.FromRgb(44, 205, 188)));
        IBrush tint = new SolidColorBrush(Color.FromArgb(18, 44, 205, 188));

        double l = x + _roiDraft.Left * w;
        double t = y + _roiDraft.Top * h;
        double r = x + _roiDraft.Right * w;
        double b = y + _roiDraft.Bottom * h;

        void Shade(double sx, double sy, double sw, double sh)
        {
            if (sw <= 0 || sh <= 0) return;
            var shape = new Rectangle
            {
                Width = sw,
                Height = sh,
                Fill = shade,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(shape, sx);
            Canvas.SetTop(shape, sy);
            OverlayCanvas.Children.Add(shape);
        }

        Shade(x, y, w, t - y);
        Shade(x, t, l - x, b - t);
        Shade(r, t, x + w - r, b - t);
        Shade(x, b, w, y + h - b);

        var outline = new Rectangle
        {
            Width = Math.Max(0, r - l),
            Height = Math.Max(0, b - t),
            Stroke = accent,
            StrokeThickness = 2 / Math.Max(0.2, _zoomFactor),
            Fill = tint,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(outline, l);
        Canvas.SetTop(outline, t);
        OverlayCanvas.Children.Add(outline);

        double handleSize = 10 / Math.Max(0.2, _zoomFactor);
        foreach (var corner in new[] { new Point(l, t), new Point(r, t), new Point(l, b), new Point(r, b) })
        {
            var handle = new Rectangle
            {
                Width = handleSize,
                Height = handleSize,
                Fill = Brushes.White,
                Stroke = accent,
                StrokeThickness = 1 / Math.Max(0.2, _zoomFactor),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(handle, corner.X - handleSize / 2);
            Canvas.SetTop(handle, corner.Y - handleSize / 2);
            OverlayCanvas.Children.Add(handle);
        }
    }
}
