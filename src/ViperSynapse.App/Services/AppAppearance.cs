using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace ViperSynapse.App.Services;

public sealed record AccentOption(string Id, string Label, Color Color)
{
    public IBrush Swatch { get; } = new ImmutableSolidColorBrush(Color);

    public override string ToString() => Label;
}

internal static class AppAppearance
{
    internal static readonly Color DefaultAccent = Color.Parse("#44D62C");

    internal static IReadOnlyList<AccentOption> Accents { get; } = Array.AsReadOnly<AccentOption>(
    [
        new("green", "Green", DefaultAccent),
        new("blue", "Blue", Color.Parse("#70BEFF")),
        new("teal", "Teal", Color.Parse("#5CDBC5")),
        new("violet", "Violet", Color.Parse("#C2A6FF")),
        new("amber", "Amber", Color.Parse("#F4C56E")),
        new("rose", "Rose", Color.Parse("#FF9AB3")),
    ]);

    internal static AccentOption Resolve(string? id) => Accents.FirstOrDefault(accent => accent.Id == id) ?? Accents[0];

    internal static void Apply(AccentOption accent)
    {
        if (Application.Current is not { } app)
            return;

        app.Resources["AccentColor"] = accent.Color;
        app.Resources["AccentBrush"] = accent.Swatch;
        app.Resources["AccentOutlineBrush"] = new ImmutableSolidColorBrush(accent.Color, 0.65);

        var fluent = app.Styles.OfType<FluentTheme>().FirstOrDefault();
        if (fluent is not null && fluent.Palettes.TryGetValue(ThemeVariant.Dark, out var palette))
            palette.Accent = accent.Color;
    }
}
