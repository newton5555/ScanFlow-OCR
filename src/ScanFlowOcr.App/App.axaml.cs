using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ScanFlowOcr.App.Configuration;
using ScanFlowOcr.Contracts;
using ScanFlowOcr.Outputs;
using ScanFlowOcr.Runtime;

namespace ScanFlowOcr.App;

public partial class App : Application
{
    public static IServiceProvider? Services { get; private set; }
    public static Func<ICameraProvider>? CameraProviderFactory { get; set; }
    public static Func<SessionProfile, ScanSession>? SessionFactory { get; set; }
    public static IOcrReaderFactory? OcrFactory { get; set; }
    public static OutputCoordinator? OutputCoordinator { get; set; }
    public static ISettingsManager? Settings => Services?.GetService(typeof(ISettingsManager)) as ISettingsManager;

    partial void ConfigureBackend();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        try { ConfigureBackend(); }
        catch (Exception ex)
        {
            CameraProviderFactory = null;
            SessionFactory = null;
            System.Diagnostics.Debug.WriteLine($"扫描服务初始化失败: {ex.Message}");
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => _ = OutputCoordinator?.DisposeAsync().AsTask();
            desktop.MainWindow = Services?.GetService(typeof(MainWindow)) as MainWindow ?? new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
