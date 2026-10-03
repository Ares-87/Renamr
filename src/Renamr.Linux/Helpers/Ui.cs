using Avalonia.Data.Converters;
using FluentAvalonia.UI.Controls;
using Renamr.Core.Models;

namespace Renamr.Linux.Helpers;

/// <summary>
/// Converter per i binding (equivalenti delle funzioni x:Bind di Renamr.App). I colori non stanno qui: le classi
/// <c>success</c>/<c>caution</c>/<c>critical</c> scelgono i pennelli Fluent negli stili, così seguono tema chiaro/scuro.
/// </summary>
public static class Ui
{
    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(value => value == 0);

    public static readonly IValueConverter Severity =
        new FuncValueConverter<bool, FAInfoBarSeverity>(hasErrors => hasErrors ? FAInfoBarSeverity.Warning : FAInfoBarSeverity.Success);

    public static readonly IValueConverter IsSuccess =
        new FuncValueConverter<PlanStatus, bool>(s => s is PlanStatus.Ready or PlanStatus.Done or PlanStatus.Simulated);

    public static readonly IValueConverter IsCaution = new FuncValueConverter<PlanStatus, bool>(s => s is PlanStatus.LowConfidence);

    public static readonly IValueConverter IsCritical = new FuncValueConverter<PlanStatus, bool>(s => s is PlanStatus.Error);

    public static readonly IValueConverter IsNeutral =
        new FuncValueConverter<PlanStatus, bool>(s => s is not (PlanStatus.Ready or PlanStatus.Done or PlanStatus.Simulated or PlanStatus.LowConfidence or PlanStatus.Error));

    /// <summary>Opacità ridotta per le righe che non verranno toccate (errori, già corretti).</summary>
    public static readonly IValueConverter RowOpacity =
        new FuncValueConverter<PlanStatus, double>(s => s is PlanStatus.Error or PlanStatus.Unchanged or PlanStatus.Skipped ? 0.6 : 1.0);
}
