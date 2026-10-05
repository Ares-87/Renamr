using Renamr.Core.BatchRename;

namespace Renamr.Services.BatchRename;

/// <summary>
/// Legge (senza mai scrivere) i dati del contenuto che servono ai segnaposto {scatto}, {artista}, {durata}…:
/// EXIF delle foto, tag della musica, risoluzione e durata dei video. Un file che TagLib non conosce, o rovinato,
/// dà <see cref="FileDetails.None"/>: il segnaposto resta vuoto e il file si rinomina lo stesso.
/// </summary>
public static class FileDetailsReader
{
    public static FileDetails Read(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
            var tag = file.Tag;
            var image = (file as TagLib.Image.File)?.ImageTag;
            var props = file.Properties;
            int? width = props is null ? null : props.PhotoWidth > 0 ? props.PhotoWidth : props.VideoWidth > 0 ? props.VideoWidth : null;
            int? height = props is null ? null : props.PhotoHeight > 0 ? props.PhotoHeight : props.VideoHeight > 0 ? props.VideoHeight : null;
            var camera = string.Join(' ', new[] { image?.Make, image?.Model }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).Distinct());
            // Molte fotocamere ripetono la marca nel modello ("Canon" + "Canon EOS 80D"): basta il modello.
            if (image?.Make is { } make && image.Model is { } model && model.Trim().StartsWith(make.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                camera = model.Trim();
            }
            return new FileDetails
            {
                DateTaken = image?.DateTime,
                Camera = Clean(camera),
                Width = width,
                Height = height,
                Duration = props?.Duration > TimeSpan.Zero ? props.Duration : null,
                Artist = Clean(tag.FirstPerformer ?? tag.FirstAlbumArtist),
                Album = Clean(tag.Album),
                Title = Clean(tag.Title),
                Track = tag.Track > 0 ? tag.Track : null,
                Year = tag.Year > 0 ? tag.Year : null,
            };
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException
                                       or NotImplementedException or ArgumentException or InvalidOperationException or NullReferenceException
                                       or IndexOutOfRangeException or OverflowException)
        {
            // TagLib su file strani lancia anche eccezioni generiche: per un segnaposto basta lasciarlo vuoto.
            return FileDetails.None;
        }
    }

    /// <summary>Niente caratteri vietati nei nomi ("AC/DC" ➔ "AC-DC") e niente spazi ai lati; vuoto = null.</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var chars = value.Trim().Select(c => c < 32 ? ' ' : c is '/' or '\\' or '|' ? '-' : c is ':' ? '.' : c is '<' or '>' or '"' or '?' or '*' ? ' ' : c);
        return string.Concat(chars).Trim();
    }
}
