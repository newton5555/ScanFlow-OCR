using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ScanFlowOcr.App.Models;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _draft;

    public AppSettings? Result { get; private set; }

    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings current, string? activePath = null)
    {
        InitializeComponent();
        _draft = current.Clone();
        LoadFromDraft();
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
        MqttBrokerBox.Text = _draft.MqttBroker;
        MqttPortBox.Text = _draft.MqttPort.ToString(CultureInfo.InvariantCulture);
        MqttTopicBox.Text = _draft.MqttTopic;
        TcpHostBox.Text = _draft.TcpHost;
        TcpPortBox.Text = _draft.TcpPort.ToString(CultureInfo.InvariantCulture);
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
        _draft.MqttBroker = MqttBrokerBox.Text?.Trim() ?? "localhost";
        _draft.MqttPort = ParseInt(MqttPortBox.Text, 1883);
        _draft.MqttTopic = MqttTopicBox.Text?.Trim() ?? "scanflow-ocr/scans";
        _draft.TcpHost = TcpHostBox.Text?.Trim() ?? "127.0.0.1";
        _draft.TcpPort = ParseInt(TcpPortBox.Text, 9100);
        _draft.OutputQueueCapacity = ParseInt(QueueCapacityBox.Text, 1000);
    }

    private void ShowError(string message)
    {
        TxtError.Text = message;
        TxtError.IsVisible = true;
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static double ParseDouble(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
}
