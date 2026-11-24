namespace ViperSynapse.App.Services;

internal static class ProjectLinks
{
    const string RepositoryUrl = "https://github.com/ColterD/Viper-Synapse";

    public static readonly Uri Repository = new(RepositoryUrl);
    public static string BuildReleaseTag => "v" + AppBranding.Version;
    static readonly string DocsUrl = RepositoryUrl + "/blob/" +
        (AppBranding.Version == "unknown" ? "main" : BuildReleaseTag) + "/docs/";

    public static readonly Uri Releases = new(RepositoryUrl + "/releases");
    public static readonly Uri LatestStableRelease = new(RepositoryUrl + "/releases/latest");
    public static readonly Uri BuildRelease = AppBranding.Version == "unknown"
        ? Releases : new(RepositoryUrl + "/releases/tag/" + BuildReleaseTag);
    public static readonly Uri Issues = new(RepositoryUrl + "/issues");
    public static readonly Uri Documentation = new(DocsUrl + "README.md");
    public static readonly Uri GettingStarted = new(DocsUrl + "getting-started.md");
    public static readonly Uri Development = new(DocsUrl + "development.md");
    public static readonly Uri Upstream = new("https://github.com/codingncaffeine/Viper-Synapse");
    public static readonly Uri OpenRazer = new("https://github.com/openrazer/openrazer");
}
