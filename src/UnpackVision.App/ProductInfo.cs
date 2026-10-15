using System.Reflection;
using System.IO;

namespace UnpackVision.App;

internal static class ProductInfo
{
    internal const string Name = "灵兔拆包智录";
    internal const string Developer = "五成";
    internal const string WebsiteUrl = "https://record.lttlt.top";
    internal const string TutorialUrl = WebsiteUrl + "/tutorial";
    internal const string RepositoryUrl = "https://github.com/907609732/lingtu-recorder";
    internal const string LatestReleaseUrl = RepositoryUrl + "/releases";
    internal const string DesktopManifestUrl = WebsiteUrl + "/desktop-update.json";
    internal const string WindowsDownloadUrl =
        RepositoryUrl + "/releases/download/v2.6.0/EcommerceUnpackRecorder-win-Setup.exe";
    internal const string AndroidDownloadUrl =
        RepositoryUrl + "/releases/download/v2.6.0/EcommerceUnpackRecorder-Android.apk";
    internal static string TelemetryEndpoint =>
        Environment.GetEnvironmentVariable("UNPACKVISION_TELEMETRY_ENDPOINT") ??
        ReadOptionalAsset("Assets/telemetry-endpoint.txt");

    internal static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "2.6.0";

    private static string ReadOptionalAsset(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
    }
}
