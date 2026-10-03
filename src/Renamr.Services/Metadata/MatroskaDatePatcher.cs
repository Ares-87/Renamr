using System.Buffers.Binary;

namespace Renamr.Services.Metadata;

/// <summary>
/// Aggiorna l'elemento <c>Segment/Info/DateUTC</c> di un MKV/WebM (nanosecondi dal 2001-01-01, int64 con segno).
/// Se l'elemento esiste lo sovrascriviamo in place (8 byte); se manca non lo inseriamo, perché richiederebbe
/// di riscrivere l'intestazione: in quel caso resta la data nel tag DATE_RELEASED scritto da TagLib.
/// </summary>
internal static class MatroskaDatePatcher
{
    private const uint EbmlHeaderId = 0x1A45DFA3;
    private const uint SegmentId = 0x18538067;
    private const uint InfoId = 0x1549A966;
    private const uint ClusterId = 0x1F43B675;
    private const uint DateUtcId = 0x4461;

    private static readonly DateTime MatroskaEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static bool TryPatch(string path, DateTime utc, out string? reason)
    {
        reason = null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        if (!TryReadElementHeader(fs, out var id, out var size) || id != EbmlHeaderId)
        {
            reason = "intestazione EBML non valida";
            return false;
        }
        fs.Position += size;

        if (!TryReadElementHeader(fs, out id, out var segmentSize) || id != SegmentId)
        {
            reason = "Segment non trovato";
            return false;
        }
        var segmentEnd = segmentSize < 0 ? fs.Length : Math.Min(fs.Length, fs.Position + segmentSize);

        if (!TryFindChild(fs, segmentEnd, InfoId, out var infoSize))
        {
            reason = "Segment/Info non trovato";
            return false;
        }
        var infoEnd = fs.Position + infoSize;

        if (!TryFindChild(fs, infoEnd, DateUtcId, out var dateSize) || dateSize != 8)
        {
            reason = "DateUTC assente";
            return false;
        }

        var ns = (utc - MatroskaEpoch).Ticks * 100; // 1 tick = 100 ns; date pre-2001 => valore negativo, ammesso
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, ns);
        fs.Write(buf);
        fs.Flush(flushToDisk: true);
        return true;
    }

    /// <summary>Scorre i fratelli fino a <paramref name="end"/>; in caso di successo lo stream è sui dati dell'elemento.</summary>
    internal static bool TryFindChild(Stream s, long end, uint wanted, out long size)
    {
        while (s.Position < end && TryReadElementHeader(s, out var id, out size))
        {
            if (id == wanted)
            {
                return true;
            }
            if (size < 0 || id == ClusterId)
            {
                break; // dimensione sconosciuta o siamo arrivati ai dati audio/video: inutile proseguire
            }
            s.Position += size;
        }
        size = -1;
        return false;
    }

    /// <summary>Legge ID (con marker) e dimensione (senza marker). size = -1 se "sconosciuta".</summary>
    internal static bool TryReadElementHeader(Stream s, out uint id, out long size)
    {
        id = 0;
        size = -1;
        if (!TryReadVint(s, keepMarker: true, out var rawId, out _) || rawId > uint.MaxValue)
        {
            return false;
        }
        id = (uint)rawId;
        if (!TryReadVint(s, keepMarker: false, out var rawSize, out var allOnes))
        {
            return false;
        }
        size = allOnes ? -1 : (long)rawSize;
        return true;
    }

    private static bool TryReadVint(Stream s, bool keepMarker, out ulong value, out bool allOnes)
    {
        value = 0;
        allOnes = false;
        var first = s.ReadByte();
        if (first <= 0)
        {
            return false; // EOF o primo byte 0x00 (lunghezza > 8, non valida)
        }

        var length = 1;
        var mask = 0x80;
        while ((first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }

        value = keepMarker ? (ulong)first : (ulong)(first & (mask - 1));
        var ones = (first & (mask - 1)) == mask - 1;
        for (var i = 1; i < length; i++)
        {
            var b = s.ReadByte();
            if (b < 0)
            {
                return false;
            }
            value = (value << 8) | (uint)b;
            ones &= b == 0xFF;
        }
        allOnes = !keepMarker && ones;
        return true;
    }
}
