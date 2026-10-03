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
