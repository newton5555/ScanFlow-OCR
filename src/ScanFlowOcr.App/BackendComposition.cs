using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using ScanFlowOcr.App.Capture;
using ScanFlowOcr.App.Configuration;
using ScanFlowOcr.Capture.FlashCap;
using ScanFlowOcr.Contracts;
using ScanFlowOcr.Imaging;
using ScanFlowOcr.Ocr.SimdPaddle;
using ScanFlowOcr.Outputs;
using ScanFlowOcr.Runtime;

namespace ScanFlowOcr.App;

[SuppressMessage("Performance", "CA1822:Mark members as static", Scope = "member", Target = "~M:ScanFlowOcr.App.App.ConfigureBackend", Justification = "Partial method ConfigureBackend is an instance lifecycle hook for backend composition.")]
public partial class App
{
    partial void ConfigureBackend()
    {
        var services = new ServiceCollection();

        // 1. Settings persistence manager
        var settingsManager = new SettingsManager();
        services.AddSingleton<ISettingsManager>(settingsManager);
        var outputs = new OutputCoordinator();
        try { outputs.Configure(settingsManager.Current.GetOutputRoutes()); } catch { /* ignore bad routes */ }
        OutputCoordinator = outputs;
        services.AddSingleton(outputs);

        // 2. Hardware and algorithmic providers
        var cameras = new ImagePlaylistCameraProvider(new FlashCapProvider());
        var ocr = new SimdPaddleOcrFactory();

        services.AddSingleton<ICameraProvider>(cameras);
        services.AddSingleton(cameras);
        services.AddSingleton<IOcrReaderFactory>(ocr);
        services.AddSingleton(ocr);

        // 3. ScanSession Factory
        Func<SessionProfile, ScanSession> sessionFactory = profile =>
        {
            if (!TurboJpegNative.TryResolveLibraryPath(out string? jpegPath, out string tjError))
                throw new InvalidOperationException("TurboJPEG 库不可用: " + tjError);
            return new ScanSession(cameras, ocr, jpegPath!, profile, outputs);
        };
        services.AddSingleton(sessionFactory);

        // 4. Windows
        services.AddTransient<MainWindow>();

        Services = services.BuildServiceProvider();

        // Compatibility bridges for static access / tests
        OcrFactory = ocr;
        CameraProviderFactory = () => cameras;
        SessionFactory = sessionFactory;
    }
}
