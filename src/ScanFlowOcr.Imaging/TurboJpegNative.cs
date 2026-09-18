namespace ScanFlowOcr.Imaging;

/// <summary>
/// Resolves libjpeg-turbo for camera JPEG decode under <c>native/{rid}/</c>.
/// Still-image JPEG continues to use StbImageSharp (not TurboJPEG).
/// </summary>
public static class TurboJpegNative
{
    public const string EnvVarName = "SCANFLOW_OCR_TURBOJPEG_PATH";

    public static string ExpectedFileName =>
        OperatingSystem.IsWindows() ? "turbojpeg.dll" : "libturbojpeg.so";

    public static string ExpectedRid =>
        OperatingSystem.IsWindows() ? "win-x64"
        : OperatingSystem.IsLinux() ? "linux-x64"
        : throw new PlatformNotSupportedException("TurboJPEG native path is only defined for win-x64 and linux-x64.");

    public static bool TryResolveLibraryPath([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? path, out string error)
    {
        path = null;
        string? fromEnv = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            if (File.Exists(fromEnv))
            {
                path = fromEnv;
                error = "";
                return true;
            }

            error = $"TurboJPEG not found at {EnvVarName}={fromEnv}. Install official libjpeg-turbo 3.2.0 or fix the path.";
            return false;
        }

        string rid;
        string fileName;
        try
        {
            rid = ExpectedRid;
            fileName = ExpectedFileName;
        }
        catch (PlatformNotSupportedException ex)
        {
            error = ex.Message;
            return false;
        }

        string candidate = Path.Combine(AppContext.BaseDirectory, "native", rid, fileName);
        if (File.Exists(candidate))
        {
            path = candidate;
            error = "";
            return true;
        }

        string alt = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", rid, fileName));
        if (File.Exists(alt))
        {
            path = alt;
            error = "";
            return true;
        }

        error =
            $"TurboJPEG native library missing (libjpeg-turbo 3.2.0). Expected {fileName} under native/{rid}/ " +
            $"(looked at: {candidate}) or set {EnvVarName}. Camera MJPEG preview cannot start without it. " +
            "See native/ORIGIN.md and README.";
        return false;
    }

    public static string ResolveLibraryPath()
    {
        if (TryResolveLibraryPath(out string? path, out string error))
            return path;
        throw new FileNotFoundException(error);
    }

    public static JpegDecoder CreateDecoder() => new(ResolveLibraryPath());
}
