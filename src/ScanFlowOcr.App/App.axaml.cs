using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ScanFlowOcr.App.Configuration;

namespace ScanFlowOcr.App;

public partial class App : Application
{
    public static ISettingsManager? Settings { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Settings = new SettingsManager();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Settings);
        base.OnFrameworkInitializationCompleted();
    }
}
