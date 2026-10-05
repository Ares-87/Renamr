using Renamr.Core.Errors;
using Renamr.Core.Models;

namespace Renamr.Core.Abstractions;

public interface IFileNameParser
{
    ParsedMediaName Parse(string fileNameOrPath);
}

public interface IMetadataResolver
{
    Task<MatchResult> ResolveAsync(MediaQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Ricerca scelta dall'utente: interroga tutti i database adatti, senza fermarsi al primo risultato sicuro,
    /// e restituisce tutti i candidati in <see cref="MatchResult.Alternatives"/> dal più probabile.
    /// </summary>
    async Task<MatchResult> SearchAllAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        var result = await ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        return result with { Alternatives = result.Best is { } best ? [best, .. result.Alternatives] : result.Alternatives };
    }
}

public interface INameTemplateEngine
{
    /// <summary>Restituisce il nuovo nome file (senza cartella), già sanificato per Windows.</summary>
    string Render(string pattern, MediaMetadata metadata, ParsedMediaName? parsed, string extension);
}

/// <summary>Scrittura della data reale (e dei campi principali) dentro il contenitore del file.</summary>
public interface IEmbeddedMetadataWriter
{
    bool CanWrite(string path);
    OperationResult Write(string path, MediaMetadata metadata) => Write(path, metadata, EmbeddedMetadataFields.All);

    /// <summary>Scrive solo i campi scelti; quelli spenti restano come sono nel file.</summary>
    OperationResult Write(string path, MediaMetadata metadata, EmbeddedMetadataFields fields);
}

/// <summary>Lettura dei tag già presenti (indizi per la musica).</summary>
public interface IEmbeddedMetadataReader
{
    MediaQuery? TryReadMusicQuery(string path);
}
