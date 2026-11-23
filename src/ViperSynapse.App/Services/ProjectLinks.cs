namespace ViperSynapse.App.Services;

internal static class ProjectLinks
{
    const string RepositoryUrl = "https://github.com/ColterD/Viper-Synapse";

    public static readonly Uri Repository = new(RepositoryUrl);
    const string DocsUrl = RepositoryUrl + "/blob/main/docs/";
    public const string FeaturedReleaseTag = "v0.0.8";

    public static readonly Uri LatestRelease = new(RepositoryUrl + "/releases/latest");
    public static readonly Uri FeaturedRelease = new(RepositoryUrl + "/releases/tag/" + FeaturedReleaseTag);
    public static readonly Uri Releases = new(RepositoryUrl + "/releases");
    public static readonly Uri Issues = new(RepositoryUrl + "/issues");
    public static readonly Uri Documentation = new(DocsUrl + "README.md");
    public static readonly Uri GettingStarted = new(DocsUrl + "getting-started.md");
    public static readonly Uri Development = new(DocsUrl + "development.md");
    public static readonly Uri Upstream = new("https://github.com/codingncaffeine/Viper-Synapse");
    public static readonly Uri OpenRazer = new("https://github.com/openrazer/openrazer");
}
