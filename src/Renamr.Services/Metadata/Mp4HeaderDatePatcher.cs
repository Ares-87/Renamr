using System.Buffers.Binary;
using System.Text;

namespace Renamr.Services.Metadata;

/// <summary>
/// Aggiorna creation_time/modification_time del box <c>moov/mvhd</c> di un MP4/MOV.
/// È la data che Esplora File mostra come "Supporto creato" (System.Media.DateEncoded).
/// La scrittura è in-place su campi a dimensione fissa: il file non cambia lunghezza.
/// </summary>
internal static class Mp4HeaderDatePatcher
{
    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static bool TryPatch(string path, DateTime utc, out string? reason)
    {
        reason = null;
        var seconds = (ulong)(utc - Epoch1904).TotalSeconds;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (!TryFindBox(fs, 0, fs.Length, "moov", out var moovData, out var moovEnd))
        {
            reason = "box moov non trovato";
            return false;
        }
        if (!TryFindBox(fs, moovData, moovEnd, "mvhd", out var mvhdData, out var mvhdEnd))
        {
            reason = "box mvhd non trovato";
            return false;
        }

        fs.Position = mvhdData;
        var version = fs.ReadByte();
        fs.Position = mvhdData + 4; // version(1) + flags(3)

        if (version == 1)
        {
            if (mvhdData + 4 + 16 > mvhdEnd) { reason = "mvhd troncato"; return false; }
            Span<byte> buf = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(buf, seconds);
            BinaryPrimitives.WriteUInt64BigEndian(buf[8..], seconds);
            fs.Write(buf);
        }
        else if (version == 0)
        {
            if (seconds > uint.MaxValue) { reason = "data oltre il limite di mvhd v0 (2040)"; return false; }
            if (mvhdData + 4 + 8 > mvhdEnd) { reason = "mvhd troncato"; return false; }
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)seconds);
            BinaryPrimitives.WriteUInt32BigEndian(buf[4..], (uint)seconds);
            fs.Write(buf);
        }
        else
        {
            reason = $"versione mvhd sconosciuta ({version})";
            return false;
        }

        fs.Flush(flushToDisk: true);
        return true;
    }

    /// <summary>Cerca un box figlio diretto nell'intervallo [start, end). Restituisce inizio dati e fine box.</summary>
    internal static bool TryFindBox(Stream s, long start, long end, string type, out long dataStart, out long boxEnd)
    {
        Span<byte> header = stackalloc byte[16];
        var pos = start;
        while (pos + 8 <= end)
        {
            s.Position = pos;
            s.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var name = Encoding.Latin1.GetString(header.Slice(4, 4));
            var headerLength = 8;

            if (size == 1)
            {
                s.ReadExactly(header.Slice(8, 8));
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = end - pos; // "fino alla fine del file"
            }

            if (size < headerLength || pos + size > end)
            {
                break; // struttura corrotta: meglio non scrivere nulla
            }

            if (name == type)
            {
                dataStart = pos + headerLength;
                boxEnd = pos + size;
                return true;
            }
            pos += size;
        }

        dataStart = boxEnd = -1;
        return false;
    }
}
