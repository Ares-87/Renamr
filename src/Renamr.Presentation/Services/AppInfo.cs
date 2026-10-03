using System.Reflection;

namespace Renamr.Presentation.Services;

/// <summary>
/// Versione mostrata accanto al nome: "Renamr v1.1.0". Il numero sta in Directory.Build.props e cresce a ogni PR;
/// il commit (aggiunto dall'SDK a InformationalVersion) distingue anche due build della stessa versione.
/// </summary>
public static class AppInfo
{
    private static readonly string Informational =
        (Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>"1.1.0".</summary>
    public static string Version => Informational.Split('+')[0];

    /// <summary>Primi 7 caratteri del commit, se la build è partita da un repository git.</summary>
    public static string? Commit => Informational.Split('+') is [_, var sha, ..] && sha.Length >= 7 ? sha[..7] : null;

    public static string Title => $"Renamr v{Version}";

    public static string Details => Commit is null ? Title : $"{Title} (commit {Commit})";
}
