namespace Renamr.Core.Matching;

/// <summary>
/// Punteggio di confidenza di un candidato rispetto alla query.
/// Pesi tarati sull'esperienza pratica: il titolo conta più di tutto, l'anno è un forte
/// discriminante per remake/omonimi, la popolarità rompe solo i pareggi.
/// </summary>
public static class ConfidenceScorer
{
    public static double Score(
        string queryTitle,
        int? queryYear,
        string candidateTitle,
        string? candidateOriginalTitle,
        int? candidateYear,
        double popularityRank = 0)
    {
        var title = Math.Max(
            TitleSimilarity.Score(queryTitle, candidateTitle),
            TitleSimilarity.Score(queryTitle, candidateOriginalTitle));

        double year;
        if (queryYear is null || candidateYear is null)
        {
            year = 0.5; // nessuna informazione: neutro
        }
        else
        {
            year = Math.Abs(queryYear.Value - candidateYear.Value) switch
            {
                0 => 1.0,
                1 => 0.8, // uscite a cavallo d'anno / date diverse per paese
                _ => 0.0,
            };
        }

        // popularityRank in [0..1]: 1 = il più popolare tra i risultati.
        var score = title * 0.75 + year * 0.20 + Math.Clamp(popularityRank, 0, 1) * 0.05;

        // Anno esplicito e sbagliato di molto: quasi certamente un omonimo.
        if (queryYear is not null && candidateYear is not null && Math.Abs(queryYear.Value - candidateYear.Value) > 1)
        {
            score *= 0.7;
        }

        return Math.Round(Math.Clamp(score, 0, 1), 3);
    }
}
