using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ScanFlowOcr.Capture.FlashCap;
using ScanFlowOcr.Ocr.SimdPaddle;
using ScanFlowOcr.Outputs;

namespace ScanFlowOcr.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Append("ScanFlow-OCR phase 1 shell.");
        Append($"OS: {Environment.OSVersion}; 64-bit process: {Environment.Is64BitProcess}");
        Append("Linked: Contracts, Imaging, Capture.FlashCap, Ocr.SimdPaddle, Outputs.");
    }

    private async void OnEnumerateCameras(object? sender, RoutedEventArgs e)
    {
        try
        {
            var provider = new FlashCapProvider();
            var devices = await provider.EnumerateAsync(CancellationToken.None);
            Append($"FlashCap: {devices.Length} MJPEG device(s).");
            foreach (var d in devices)
                Append($"  - {d.DisplayName} [{d.Id.ProviderId}/{d.Id.DeviceKey}]");
        }
        catch (Exception ex)
        {
            Append($"Enumerate failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnOcrPlaceholder(object? sender, RoutedEventArgs e)
    {
        Append($"OCR factory: {typeof(SimdPaddleOcrFactory).FullName}");
        Append($"ProviderId={SimdPaddleOcrMetadata.ProviderId}; params={SimdPaddleOcrMetadata.Parameters.Length}");
    }

    private async void OnKeyboardInfo(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sink = KeyboardOutputSink.Create(new KeyboardRoute("notepad.exe"));
            Append($"Keyboard sink: {sink.Descriptor.Id} -> {sink.GetType().Name}");
            await sink.DisposeAsync();
        }
        catch (Exception ex)
        {
            Append($"Keyboard sink: {ex.Message}");
        }
    }

    private void Append(string line) =>
        Log.Text = string.IsNullOrEmpty(Log.Text) ? line : Log.Text + Environment.NewLine + line;
}
