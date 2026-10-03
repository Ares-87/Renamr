namespace Renamr.Core.Models;

/// <summary>Avanzamento in tempo reale, pensato per IProgress&lt;T&gt; (marshalling sul thread UI automatico).</summary>
public readonly record struct RenameProgress(int Processed, int Total, RenamePlanEntry? LastEntry, string Phase)
{
    public double Fraction => Total == 0 ? 0 : (double)Processed / Total;
}
