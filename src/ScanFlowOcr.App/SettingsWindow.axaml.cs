using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ScanFlowOcr.App.Capture;
using ScanFlowOcr.App.Models;
using ScanFlowOcr.Contracts;
using ScanFlowOcr.Outputs;

namespace ScanFlowOcr.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _draft;
    private readonly OutputCoordinator? _coordinator;
    private readonly ImmutableArray<CameraDescriptor> _availableCameras;
    private readonly ICameraProvider? _cameraProvider;

    public AppSettings? Result { get; private set; }
    public CameraDescriptor? SelectedDevice { get; private set; }
    public CaptureMode? SelectedMode { get; private set; }

    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(
        AppSettings current,
        string? activePath = null,
        OutputCoordinator? coordinator = null,
        ImmutableArray<CameraDescriptor> availableCameras = default,
        ICameraProvider? cameraProvider = null,
        int currentDeviceIndex = -1,
        int currentModeIndex = -1)
    {
        InitializeComponent();
        _draft = current.Clone();
        _coordinator = coordinator;
        _availableCameras = availableCameras;
        _cameraProvider = cameraProvider;
        if (currentDeviceIndex >= 0)
        {
            _draft.PreferredCameraIndex = currentDeviceIndex;
        }
        if (currentModeIndex >= 0)
        {
            _draft.PreferredModeIndex = currentModeIndex;
        }
        LoadFromDraft();
        RefreshOutputStatus();
        if (TxtPath is not null && !string.IsNullOrEmpty(activePath))
        {
            TxtPath.Text = $"配置文件 · {System.IO.Path.GetFileName(activePath)}";
            ToolTip.SetTip(TxtPath, activePath);
        }

        if (OperatingSystem.IsLinux())
        {
            WindowDecorations = Avalonia.Controls.WindowDecorations.Full;
            BtnCloseWindow.IsVisible = false;
        }
    }

    private void OnTitleBarPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCloseWindowClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    public void SelectCameraTab()
    {
        SettingsTabControl.SelectedItem = TabCameraSettings;
    }

    public void SelectOutputTab()
    {
        SettingsTabControl.SelectedItem = TabOutputSettings;
    }

    private void LoadFromDraft()
    {
        SelectTagged(ModelCombo, _draft.GetOcrModel());
        SelectTagged(CmbOcrDetSide, _draft.OcrParameters.TryGetValue("detectSideLength", out var detSide) ? detSide : "640");
        SelectTagged(CmbOcrLayout, _draft.OcrLayout == OcrLayout.SingleLine ? "SingleLine" : "TextBlock");
        ChkOcrDirection.IsChecked = !_draft.OcrParameters.TryGetValue("useDirectionClassification", out var dir) || !bool.TryParse(dir, out var dirOn) || dirOn;
        TxtOcrMinConfidence.Text = _draft.OcrParameters.TryGetValue("minConfidencePercent", out var minConf) ? minConf : "90";
        OcrTimeoutBox.Text = _draft.OcrTimeoutMs.ToString(CultureInfo.InvariantCulture);
        RbDedupeSession.IsChecked = _draft.DedupeMode == DedupeMode.Session;
        RbDedupeCooldown.IsChecked = _draft.DedupeMode == DedupeMode.Cooldown;
        RbDedupeUntilAbsent.IsChecked = _draft.DedupeMode == DedupeMode.UntilAbsent;
        RbDedupeIntraFrame.IsChecked = _draft.DedupeMode == DedupeMode.IntraFrame;
        DedupeIntervalBox.Text = _draft.DedupeIntervalMs.ToString(CultureInfo.InvariantCulture);

        ChkEnableRoi.IsChecked = _draft.EnableRoi;
        ChkShowGuides.IsChecked = _draft.ShowPreviewGuides;
        RoiXBox.Text = _draft.RoiX.ToString("0.##", CultureInfo.InvariantCulture);
        RoiYBox.Text = _draft.RoiY.ToString("0.##", CultureInfo.InvariantCulture);
        RoiWBox.Text = _draft.RoiWidth.ToString("0.##", CultureInfo.InvariantCulture);
        RoiHBox.Text = _draft.RoiHeight.ToString("0.##", CultureInfo.InvariantCulture);

        ChkPreviewEnabled.IsChecked = _draft.PreviewEnabled;
        PreviewFpsBox.Text = _draft.PreviewMaxFps.ToString(CultureInfo.InvariantCulture);
        PreviewMaxWBox.Text = _draft.PreviewMaxWidth.ToString(CultureInfo.InvariantCulture);
        PreviewMaxHBox.Text = _draft.PreviewMaxHeight.ToString(CultureInfo.InvariantCulture);

        PreferredCameraBox.Text = _draft.PreferredCameraIndex.ToString(CultureInfo.InvariantCulture);
        PreferredModeBox.Text = _draft.PreferredModeIndex.ToString(CultureInfo.InvariantCulture);

        if (!_availableCameras.IsDefaultOrEmpty)
        {
            SettingsCameraDeviceCombo.ItemsSource = _availableCameras.Select(c => c.DisplayName).ToList();
            int camIdx = Math.Clamp(_draft.PreferredCameraIndex, 0, _availableCameras.Length - 1);
            SettingsCameraDeviceCombo.SelectedIndex = camIdx;
        }

        RbOutNone.IsChecked = !_draft.MqttEnabled && !_draft.TcpEnabled && !_draft.KeyboardEnabled;
        RbOutKeyboard.IsChecked = _draft.KeyboardEnabled;
        RbOutMqtt.IsChecked = _draft.MqttEnabled;
        RbOutTcp.IsChecked = _draft.TcpEnabled;
        KeyboardTargetBox.Text = _draft.KeyboardTargetProcess;
        SelectTagged(KeyboardSuffixCombo, _draft.KeyboardSuffix.ToString());
        SelectTagged(KeyboardSendModeCombo, _draft.KeyboardSendMode.ToString());
        KeyboardSeparatorBox.Text = _draft.KeyboardSeparator;
        SelectTagged(KeyboardTargetActionCombo, _draft.KeyboardTargetAction.ToString());
        MqttBrokerBox.Text = _draft.MqttBroker;
        MqttPortBox.Text = _draft.MqttPort.ToString(CultureInfo.InvariantCulture);
        MqttTopicBox.Text = _draft.MqttTopic;
        MqttClientIdBox.Text = _draft.MqttClientId;
        SelectTagged(MqttQosCombo, _draft.MqttQos.ToString(CultureInfo.InvariantCulture));
        MqttUsernameBox.Text = _draft.MqttUsername;
        MqttTlsBox.IsChecked = _draft.MqttTls;
        TcpHostBox.Text = _draft.TcpHost;
        TcpPortBox.Text = _draft.TcpPort.ToString(CultureInfo.InvariantCulture);
        TcpTlsBox.IsChecked = _draft.TcpTls;
        QueueCapacityBox.Text = _draft.OutputQueueCapacity.ToString(CultureInfo.InvariantCulture);
        UpdateConditionalSections();
    }

    private async void OnSettingsCameraDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        int index = SettingsCameraDeviceCombo.SelectedIndex;
        if (_cameraProvider is null || _availableCameras.IsDefaultOrEmpty || index < 0 || index >= _availableCameras.Length)
        {
            SettingsCameraModeCombo.ItemsSource = null;
            return;
        }

        try
        {
            var device = _availableCameras[index];
            var modes = await _cameraProvider.GetModesAsync(device.Id, CancellationToken.None).ConfigureAwait(true);
            SettingsCameraModeCombo.ItemsSource = modes.Select(m =>
                ImagePlaylistCameraProvider.IsImageDevice(device.Id)
                    ? $"{m.Width}×{m.Height} · {m.FpsDenominator} ms/张"
                    : $"{m.Width}×{m.Height} @ {m.FpsNumerator}/{Math.Max(1, m.FpsDenominator)} · {(m.Encoding == FrameEncoding.Jpeg ? "MJPEG" : "RGB24/32")}").ToList();
            SettingsCameraModeCombo.Tag = modes;
            int modeIdx = Math.Clamp(_draft.PreferredModeIndex, 0, Math.Max(0, modes.Length - 1));
            if (modes.Length > 0)
                SettingsCameraModeCombo.SelectedIndex = modeIdx;
        }
        catch
        {
            SettingsCameraModeCombo.ItemsSource = null;
        }
    }

    private void SelectModel(string model)
    {
        for (int i = 0; i < ModelCombo.ItemCount; i++)
        {
            if (ModelCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), model, StringComparison.OrdinalIgnoreCase))
            {
                ModelCombo.SelectedIndex = i;
                return;
            }
        }
        ModelCombo.SelectedIndex = 0;
    }

    private void OnResetRoiToFull(object? sender, RoutedEventArgs e)
    {
        RoiXBox.Text = "0";
        RoiYBox.Text = "0";
        RoiWBox.Text = "100";
        RoiHBox.Text = "100";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOutputRouteChanged(object? sender, RoutedEventArgs e) => UpdateConditionalSections();

    private void OnRoiEnabledChanged(object? sender, RoutedEventArgs e) => UpdateConditionalSections();

    private void OnDedupeStrategyChanged(object? sender, RoutedEventArgs e) => UpdateConditionalSections();

    private void UpdateConditionalSections()
    {
        if (OutputKeyboardSection is not null)
            OutputKeyboardSection.IsVisible = RbOutKeyboard.IsChecked == true;
        if (OutputMqttSection is not null)
            OutputMqttSection.IsVisible = RbOutMqtt.IsChecked == true;
        if (OutputTcpSection is not null)
            OutputTcpSection.IsVisible = RbOutTcp.IsChecked == true;
        if (RoiFieldsPanel is not null)
            RoiFieldsPanel.IsEnabled = ChkEnableRoi.IsChecked == true;
        if (DedupeIntervalRow is not null)
            DedupeIntervalRow.IsVisible = RbDedupeCooldown.IsChecked == true;
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            ApplyUiToDraft();
            if (!_draft.Validate(out string? error))
            {
                ShowError(error ?? "配置无效。");
                return;
            }
            if (!_availableCameras.IsDefaultOrEmpty &&
                SettingsCameraDeviceCombo.SelectedIndex >= 0 &&
                SettingsCameraDeviceCombo.SelectedIndex < _availableCameras.Length)
            {
                SelectedDevice = _availableCameras[SettingsCameraDeviceCombo.SelectedIndex];
            }
            if (SettingsCameraModeCombo.Tag is ImmutableArray<CaptureMode> modes &&
                SettingsCameraModeCombo.SelectedIndex >= 0 &&
                SettingsCameraModeCombo.SelectedIndex < modes.Length)
            {
                SelectedMode = modes[SettingsCameraModeCombo.SelectedIndex];
            }
            Result = _draft.Clone();
            Close(Result);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        await Task.CompletedTask;
    }

    private void ApplyUiToDraft()
    {
        string model = ModelCombo.SelectedItem is ComboBoxItem mi && mi.Tag is string s ? s : "tiny";
        _draft.SetOcrModel(model);

        _draft.OcrLayout = CmbOcrLayout.SelectedItem is ComboBoxItem layoutItem &&
                           string.Equals(layoutItem.Tag?.ToString(), "SingleLine", StringComparison.OrdinalIgnoreCase)
            ? OcrLayout.SingleLine
            : OcrLayout.TextBlock;

        if (CmbOcrDetSide.SelectedItem is ComboBoxItem detItem && detItem.Tag is string detTag)
            _draft.OcrParameters["detectSideLength"] = detTag;
        else
            _draft.OcrParameters["detectSideLength"] = "640";

        _draft.OcrParameters["useDirectionClassification"] = ChkOcrDirection.IsChecked == true ? "true" : "false";

        int minConf = Math.Clamp(ParseInt(TxtOcrMinConfidence.Text, 90), 0, 100);
        _draft.OcrParameters["minConfidencePercent"] = minConf.ToString(CultureInfo.InvariantCulture);

        int timeout = Math.Clamp(ParseInt(OcrTimeoutBox.Text, 5000), 500, 30000);
        _draft.OcrTimeoutMs = timeout;
        _draft.DedupeMode =
            RbDedupeCooldown.IsChecked == true ? DedupeMode.Cooldown :
            RbDedupeUntilAbsent.IsChecked == true ? DedupeMode.UntilAbsent :
            RbDedupeIntraFrame.IsChecked == true ? DedupeMode.IntraFrame :
            DedupeMode.Session;
        _draft.DedupeIntervalMs = ParseInt(DedupeIntervalBox.Text, 1500);

        _draft.EnableRoi = ChkEnableRoi.IsChecked == true;
        _draft.ShowPreviewGuides = ChkShowGuides.IsChecked == true;
        _draft.RoiX = ParseDouble(RoiXBox.Text, 10);
        _draft.RoiY = ParseDouble(RoiYBox.Text, 10);
        _draft.RoiWidth = ParseDouble(RoiWBox.Text, 80);
        _draft.RoiHeight = ParseDouble(RoiHBox.Text, 80);

        _draft.PreviewEnabled = ChkPreviewEnabled.IsChecked == true;
        _draft.PreviewMaxFps = ParseInt(PreviewFpsBox.Text, 15);
        _draft.PreviewMaxWidth = ParseInt(PreviewMaxWBox.Text, 0);
        _draft.PreviewMaxHeight = ParseInt(PreviewMaxHBox.Text, 0);

        _draft.PreferredCameraIndex = SettingsCameraDeviceCombo.SelectedIndex >= 0
            ? SettingsCameraDeviceCombo.SelectedIndex
            : ParseInt(PreferredCameraBox.Text, 0);
        _draft.PreferredModeIndex = SettingsCameraModeCombo.SelectedIndex >= 0
            ? SettingsCameraModeCombo.SelectedIndex
            : ParseInt(PreferredModeBox.Text, 0);

        _draft.KeyboardEnabled = RbOutKeyboard.IsChecked == true;
        _draft.MqttEnabled = RbOutMqtt.IsChecked == true;
        _draft.TcpEnabled = RbOutTcp.IsChecked == true;
        _draft.KeyboardTargetProcess = KeyboardTargetBox.Text?.Trim() ?? "notepad.exe";
        if (KeyboardSuffixCombo.SelectedItem is ComboBoxItem suffixItem &&
            Enum.TryParse<KeyboardSuffix>(suffixItem.Tag?.ToString(), out var suffix))
            _draft.KeyboardSuffix = suffix;
        if (KeyboardSendModeCombo.SelectedItem is ComboBoxItem sendModeItem &&
            Enum.TryParse<KeyboardSendMode>(sendModeItem.Tag?.ToString(), out var sendMode))
            _draft.KeyboardSendMode = sendMode;
        _draft.KeyboardSeparator = KeyboardSeparatorBox.Text ?? " | ";
        if (KeyboardTargetActionCombo.SelectedItem is ComboBoxItem actionItem &&
            Enum.TryParse<KeyboardTargetAction>(actionItem.Tag?.ToString(), out var action))
            _draft.KeyboardTargetAction = action;
        _draft.MqttBroker = MqttBrokerBox.Text?.Trim() ?? "localhost";
        _draft.MqttPort = ParseInt(MqttPortBox.Text, 1883);
        _draft.MqttTopic = MqttTopicBox.Text?.Trim() ?? "scanflow-ocr/scans";
        _draft.MqttClientId = MqttClientIdBox.Text?.Trim() ?? "";
        _draft.MqttUsername = string.IsNullOrWhiteSpace(MqttUsernameBox.Text) ? null : MqttUsernameBox.Text.Trim();
        _draft.MqttTls = MqttTlsBox.IsChecked == true;
        if (MqttQosCombo.SelectedItem is ComboBoxItem qosItem &&
            int.TryParse(qosItem.Tag?.ToString(), CultureInfo.InvariantCulture, out int qos))
            _draft.MqttQos = qos;
        if (ClearMqttPasswordBox.IsChecked == true) _draft.MqttProtectedPassword = null;
        else if (!string.IsNullOrEmpty(MqttPasswordBox.Text))
            _draft.MqttProtectedPassword = MqttRoute.ProtectPassword(MqttPasswordBox.Text);
        _draft.TcpHost = TcpHostBox.Text?.Trim() ?? "127.0.0.1";
        _draft.TcpPort = ParseInt(TcpPortBox.Text, 9100);
        _draft.TcpTls = TcpTlsBox.IsChecked == true;
        _draft.OutputQueueCapacity = ParseInt(QueueCapacityBox.Text, 1000);
    }

    private void RefreshOutputStatus()
    {
        if (_coordinator is null)
        {
            OutputQueueStatusText.Text = "输出队列未连接。";
            RetryUncertainButton.IsEnabled = false;
            ResendCompletedButton.IsEnabled = false;
            return;
        }
        var status = _coordinator.GetStatus();
        OutputQueueStatusText.Text = $"待发送 {status.PendingCount} · 待核对 {status.UncertainCount} · 已完成 {status.DeliveredCount}"
            + (string.IsNullOrWhiteSpace(status.LastError) ? "" : $"\n最近错误: {status.LastError}");
        RetryUncertainButton.IsEnabled = status.UncertainCount > 0;
        ResendCompletedButton.IsEnabled = status.DeliveredCount > 0;
    }

    private void OnRefreshOutputStatus(object? sender, RoutedEventArgs e) => RefreshOutputStatus();

    private async void OnRetryUncertain(object? sender, RoutedEventArgs e) =>
        await ConfirmQueueActionAsync("重试待核对记录", "这些记录可能已被目标收到。确认后将再次发送，可能产生重复数据。",
            () => _coordinator!.RetryUncertain());

    private async void OnResendCompleted(object? sender, RoutedEventArgs e) =>
        await ConfirmQueueActionAsync("重发已完成记录", "已完成的记录将重新入队并再次发送，目标可能收到重复数据。",
            () => _coordinator!.ResendCompleted());

    private async Task ConfirmQueueActionAsync(string title, string warning, Func<int> action)
    {
        if (_coordinator is null) return;
        var dialog = new Window
        {
            Title = title, Width = 420, Height = 170, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                        Children = { new Button { Content = "取消", IsCancel = true }, new Button { Content = "确认", IsDefault = true } }
                    }
                }
            }
        };
        var buttons = ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children;
        ((Button)buttons[0]).Click += (_, _) => dialog.Close(false);
        ((Button)buttons[1]).Click += (_, _) => dialog.Close(true);
        if (!await dialog.ShowDialog<bool>(this)) return;
        try
        {
            int count = action();
            RefreshOutputStatus();
            OutputQueueStatusText.Text += $"\n已重新入队 {count} 条。";
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void ShowError(string message)
    {
        TxtError.Text = message;
        BannerError.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static void SelectTagged(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem comboItem &&
                string.Equals(comboItem.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = comboItem;
                return;
            }
        }
    }

    private static double ParseDouble(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
}
