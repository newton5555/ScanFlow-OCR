namespace ScanFlowOcr.Imaging;

/// <summary>
/// Resolves libjpeg-turbo for camera JPEG decode under <c>native/{rid}/</c>.
/// Still-image JPEG continues to use StbImageSharp (not TurboJPEG).
/// </summary>
public static class TurboJpegNative
{
    public const string EnvVarName = "SCANFLOW_OCR_TURBOJPEG_PATH";

    public static string ResolveLibraryPath()
    {
        string? fromEnv = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        string rid = OperatingSystem.IsWindows() ? "win-x64"
            : OperatingSystem.IsLinux() ? "linux-x64"
            : throw new PlatformNotSupportedException("TurboJPEG native path is only defined for win-x64 and linux-x64.");

        string fileName = OperatingSystem.IsWindows() ? "turbojpeg.dll" : "libturbojpeg.so";
        string candidate = Path.Combine(AppContext.BaseDirectory, "native", rid, fileName);
        if (File.Exists(candidate))
            return candidate;

        string alt = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", rid, fileName));
        if (File.Exists(alt))
            return alt;

        throw new FileNotFoundException(
            $"TurboJPEG native library not found. Place {fileName} under native/{rid}/ or set {EnvVarName}. Looked at: {candidate}");
    }

    public static JpegDecoder CreateDecoder() => new(ResolveLibraryPath());
}
