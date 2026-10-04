namespace Renamr.Presentation.Animation;

/// <summary>
/// Il "richiamo" della tazzina del caffè, uguale su Windows e Linux: ogni tanto oscilla e il vapore sale.
/// Le viste chiedono qui la posa per ogni fotogramma, così il movimento è lo stesso nelle due app.
/// </summary>
public static class CoffeeWiggle
{
    /// <summary>Durata di un'oscillazione.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1600);

    /// <summary>Attesa prima della prima oscillazione, dopo l'apertura della finestra.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(6);

    /// <summary>Pausa tra un'oscillazione e l'altra: abbastanza per farsi notare senza disturbare.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);

    /// <summary>Intervallo tra i fotogrammi (circa 60 al secondo).</summary>
    public static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// Posa al tempo indicato dall'inizio dell'oscillazione: rotazione della tazzina in gradi, spostamento verso l'alto
    /// del vapore in pixel (su 24) e sua opacità. Fuori dalla durata la tazzina è ferma.
    /// </summary>
    public static (double Angle, double SteamRise, double SteamOpacity) At(TimeSpan elapsed)
    {
        var t = elapsed.TotalMilliseconds / Duration.TotalMilliseconds;
        if (t <= 0 || t >= 1)
        {
            return (0, 0, Rest);
        }
        var fade = 1 - t;
        var angle = 14 * Math.Sin(t * 3 * 2 * Math.PI) * fade * fade;
        var rise = 3 * Math.Sin(t * Math.PI);
        var opacity = Rest + (1 - Rest) * Math.Sin(t * Math.PI);
        return (angle, rise, opacity);
    }

    /// <summary>Opacità del vapore a riposo.</summary>
    public const double Rest = 0.55;
}
