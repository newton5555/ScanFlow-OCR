using ScanFlowOcr.App.Models;

namespace ScanFlowOcr.App.Configuration;

public interface ISettingsManager : IDisposable
{
    AppSettings Current { get; }
    string ActiveFilePath { get; }
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
    void Reload();
    AppSettings CreateDefault();
    event Action<AppSettings>? SettingsChanged;
}
