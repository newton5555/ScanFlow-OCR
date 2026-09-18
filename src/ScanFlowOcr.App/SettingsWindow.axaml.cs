using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ScanFlowOcr.App.Models;
using ScanFlowOcr.Contracts;
using ScanFlowOcr.Outputs;

namespace ScanFlowOcr.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _draft;
    private readonly OutputCoordinator? _coordinator;

    public AppSettings? Result { get; private set; }

    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings current, string? activePath = null, OutputCoordinator? coordinator = null)
    {
        InitializeComponent();
        _draft = current.Clone();
        _coordinator = coordinator;
        LoadFromDraft();
        RefreshOutputStatus();
        if (TxtPath is not null && !string.IsNullOrEmpty(activePath))
            TxtPath.Text = $"配置文件: {activePath}";
    }

    private void LoadFromDraft()
    {
        SelectModel(_draft.GetOcrModel());
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

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

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
        string model = ModelCombo.SelectedItem is ComboBoxItem mi && mi.Content is string s ? s : "tiny";
        _draft.SetOcrModel(model);
        _draft.OcrTimeoutMs = ParseInt(OcrTimeoutBox.Text, 5000);
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

        _draft.PreferredCameraIndex = ParseInt(PreferredCameraBox.Text, 0);
        _draft.PreferredModeIndex = ParseInt(PreferredModeBox.Text, 0);

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
        TxtError.IsVisible = true;
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
