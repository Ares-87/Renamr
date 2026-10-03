namespace Renamr.Presentation.ViewModels;

/// <summary>Lingua dei titoli, con il nome da mostrare nel selettore rapido.</summary>
public sealed record LanguageOption(string Tag, string Label)
{
    public static IReadOnlyList<LanguageOption> All { get; } =
    [
        new("it-IT", "Italiano"),
        new("en-US", "English"),
        new("es-ES", "Español"),
        new("fr-FR", "Français"),
        new("de-DE", "Deutsch"),
        new("ja-JP", "日本語"),
    ];

    public static LanguageOption For(string? tag) =>
        All.FirstOrDefault(l => string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase))
        ?? new LanguageOption(tag ?? "en-US", tag ?? "English");

    public override string ToString() => Label;
}
