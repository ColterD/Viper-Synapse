namespace ViperSynapse.App.Services;

internal static class ProjectLinks
{
    const string RepositoryUrl = "https://github.com/ColterD/Viper-Synapse";

    internal static readonly Uri Repository = new(RepositoryUrl);
    internal static readonly Uri Releases = new(RepositoryUrl + "/releases");
    internal static readonly Uri GettingStarted = new(RepositoryUrl + "/blob/main/docs/getting-started.md");
}
