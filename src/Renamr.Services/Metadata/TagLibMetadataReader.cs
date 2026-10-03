using Renamr.Core.Abstractions;
using Renamr.Core.Models;

namespace Renamr.Services.Metadata;

/// <summary>Legge i tag esistenti dei file audio: sono il primo indizio per MusicBrainz.</summary>
public sealed class TagLibMetadataReader : IEmbeddedMetadataReader
{
    public MediaQuery? TryReadMusicQuery(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
            var tag = file.Tag;
            if (string.IsNullOrWhiteSpace(tag.Title))
            {
                return null;
            }
            return new MediaQuery
            {
                Kind = MediaKind.Music,
                Title = tag.Title,
                Artist = tag.FirstPerformer ?? tag.FirstAlbumArtist,
                Album = tag.Album,
                Year = tag.Year > 0 ? (int)tag.Year : null,
                Episode = tag.Track > 0 ? (int)tag.Track : null,
                Duration = file.Properties?.Duration,
                FilePath = path,
            };
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
