using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Models;
using TagLib;
using File = System.IO.File;

namespace Renamr.Services.Metadata;

/// <summary>
/// Scrive la data reale (e i campi principali) <b>dentro</b> il contenitore, con TagLibSharp.
/// <para>
/// Sicurezza: TagLib riscrive il file quando il blocco tag cresce. Per non rischiare un MKV da 20 GB
/// troncato da un crash, la scrittura avviene su una copia temporanea nella stessa cartella e poi
/// <see cref="File.Replace(string, string, string?)"/> (scambio atomico su NTFS). Oltre la soglia
/// <see cref="CopyOnWriteMaxBytes"/> i tag non vengono toccati: si aggiorna solo la data nell'intestazione
/// a dimensione fissa (mvhd MP4 / DateUTC MKV), che è una scrittura di 4-8 byte senza spostamenti.
/// </para>
/// </summary>
public sealed class TagLibMetadataWriter(ILogger<TagLibMetadataWriter>? logger = null) : IEmbeddedMetadataWriter
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".m4v", ".mp4", ".mov", ".mkv", ".webm", ".mka", ".wma", ".wmv", ".asf", ".avi", ".wav", ".aiff",
    };

    /// <summary>Atomo iTunes "©day" (TagLib non espone pubblicamente BoxType.Day).</summary>
    private static readonly ByteVector AppleDayBox = new([0xA9, (byte)'d', (byte)'a', (byte)'y']);

    private readonly ILogger _log = logger ?? NullLogger<TagLibMetadataWriter>.Instance;

    public long CopyOnWriteMaxBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    public bool CanWrite(string path) => Supported.Contains(Path.GetExtension(path));

    public OperationResult Write(string path, MediaMetadata metadata)
    {
        if (!CanWrite(path))
        {
            return OperationResult.Ok([RenamrError.From(RenamrErrorCode.MetadataFormatUnsupported, Path.GetExtension(path))]);
        }

        var warnings = new List<RenamrError>();
        var length = new FileInfo(path).Length;

        if (length <= CopyOnWriteMaxBytes && HasFreeSpaceFor(path, length))
        {
            var result = WriteTagsCopyOnWrite(path, metadata);
            if (!result.Succeeded)
            {
                return result;
            }
            warnings.AddRange(result.Warnings);
        }
        else
        {
            warnings.Add(RenamrError.From(RenamrErrorCode.MetadataWriteFailed,
                "File troppo grande per una riscrittura sicura dei tag: aggiornata solo la data nell'intestazione."));
        }

        // Data "di codifica" nell'intestazione del contenitore: è quella che Esplora File mostra come "Supporto creato".
        if (metadata.ReleaseDate is { } date)
        {
            var utc = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            string? reason = null;
            var patched = ext switch
            {
                ".mp4" or ".m4v" or ".m4a" or ".mov" => Mp4HeaderDatePatcher.TryPatch(path, utc, out reason),
                ".mkv" or ".webm" or ".mka" => MatroskaDatePatcher.TryPatch(path, utc, out reason),
                _ => true,
            };
            if (!patched)
            {
                _log.LogDebug("Data intestazione non aggiornata per {Path}: {Reason}", path, reason);
            }
        }

        return OperationResult.Ok(warnings);
    }

    private OperationResult WriteTagsCopyOnWrite(string path, MediaMetadata metadata)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileNameWithoutExtension(path)}.renamr-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        try
        {
            File.Copy(path, temp, overwrite: false);
            File.SetAttributes(temp, FileAttributes.Hidden | FileAttributes.Temporary);

            bool dateWritten;
            using (var file = TagLib.File.Create(temp))
            {
                dateWritten = ApplyTags(file, metadata);
                file.Save();
            }

            File.SetAttributes(temp, FileAttributes.Normal);
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);

            return dateWritten || metadata.ReleaseDate is null
                ? OperationResult.Ok()
                : OperationResult.Ok([RenamrError.From(RenamrErrorCode.MetadataFormatUnsupported, "Salvato solo l'anno")]);
        }
        catch (Exception ex) when (ex is UnsupportedFormatException or CorruptFileException)
        {
            return OperationResult.Ok([RenamrError.From(RenamrErrorCode.MetadataFormatUnsupported, ex.Message)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotImplementedException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Scrittura tag fallita per {Path}", path);
            // L'originale non è mai stato toccato: è solo un avviso.
            return OperationResult.Ok([RenamrError.From(RenamrErrorCode.MetadataWriteFailed, ex.Message)]);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>Applica i campi comuni e la data completa nel formato nativo di ogni contenitore.</summary>
    internal static bool ApplyTags(TagLib.File file, MediaMetadata md)
    {
        // Per MP3 garantiamo un ID3v2.4 (TDRC con data completa non esiste in v2.3).
        if (file.MimeType.Contains("mp3", StringComparison.OrdinalIgnoreCase) || file is TagLib.Mpeg.AudioFile)
        {
            file.GetTag(TagTypes.Id3v2, create: true);
        }

        var tag = file.Tag;
        if (md.Year is { } year)
        {
            tag.Year = (uint)year;
        }

        switch (md.Kind)
        {
            case MediaKind.Movie:
                tag.Title = md.Title;
                tag.Description = md.Overview;
                break;
            case MediaKind.Episode or MediaKind.Anime:
                tag.Title = md.EpisodeTitle ?? md.Title;
                tag.Album = md.Title;
                if (md.Episode is { } ep) tag.Track = (uint)ep;
                if (md.Season is { } s) tag.Disc = (uint)s;
                tag.Description = md.Overview;
                break;
            case MediaKind.Music:
                tag.Title = md.Title;
                if (md.Artist is not null) tag.Performers = [md.Artist];
                if ((md.AlbumArtist ?? md.Artist) is { } aa) tag.AlbumArtists = [aa];
                if (md.Album is not null) tag.Album = md.Album;
                if (md.TrackNumber is { } t) tag.Track = (uint)t;
                if (md.DiscNumber is { } d) tag.Disc = (uint)d;
                break;
        }
        if (md.Genres.Count > 0)
        {
            tag.Genres = [.. md.Genres];
        }

        if (md.ReleaseDate is not { } date)
        {
            return false;
        }

        var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var written = false;

        if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            id3.Version = 4;
            id3.SetTextFrame("TDRC", iso); // data di registrazione/uscita (Esplora File: "Anno")
            id3.SetTextFrame("TDRL", iso); // data di rilascio
            written = true;
        }
        if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            xiph.SetField("DATE", iso);
            xiph.SetField("ORIGINALDATE", iso);
            written = true;
        }
        if (file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
        {
            apple.SetText(AppleDayBox, iso + "T12:00:00Z");
            if (md.Kind is MediaKind.Episode or MediaKind.Anime)
            {
                apple.SetText("tvsh", md.Title);
            }
            written = true;
        }
        if (file.GetTag(TagTypes.Matroska) is TagLib.Matroska.Tag mkv)
        {
            mkv.Set("DATE_RELEASED", null, iso);
            written = true;
        }
        if (file.GetTag(TagTypes.RiffInfo) is TagLib.Riff.InfoTag riff)
        {
            riff.SetValue("ICRD", new[] { iso });
            written = true;
        }
        if (file.GetTag(TagTypes.Asf) is TagLib.Asf.Tag asf)
        {
            asf.SetDescriptorString(iso, "WM/OriginalReleaseTime");
            written = true;
        }
        return written;
    }

    private static bool HasFreeSpaceFor(string path, long length)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null || new DriveInfo(root).AvailableFreeSpace > length + 64 * 1024 * 1024;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "File temporaneo non eliminato: {Path}", path);
        }
    }
}
