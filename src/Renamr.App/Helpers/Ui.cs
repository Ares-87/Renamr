using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Renamr.Core.Models;

namespace Renamr.App.Helpers;

/// <summary>
/// Funzioni per x:Bind (al posto dei converter): compilate, tipizzate, nessuna reflection.
/// I pennelli sono quelli di sistema Fluent, quindi seguono automaticamente tema chiaro/scuro e contrasto elevato.
/// </summary>
public static class Ui
{
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool IsZero(int value) => value == 0;

    public static bool Not(bool value) => !value;

    public static InfoBarSeverity Severity(bool hasErrors) => hasErrors ? InfoBarSeverity.Warning : InfoBarSeverity.Success;

    public static Visibility HasText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Brush StatusBackground(PlanStatus status) => Brush(status switch
    {
        PlanStatus.Ready or PlanStatus.Done or PlanStatus.Simulated => "SystemFillColorSuccessBackgroundBrush",
        PlanStatus.LowConfidence => "SystemFillColorCautionBackgroundBrush",
        PlanStatus.Error => "SystemFillColorCriticalBackgroundBrush",
        _ => "SystemFillColorNeutralBackgroundBrush",
    });

    public static Brush StatusForeground(PlanStatus status) => Brush(status switch
    {
        PlanStatus.Ready or PlanStatus.Done or PlanStatus.Simulated => "SystemFillColorSuccessBrush",
        PlanStatus.LowConfidence => "SystemFillColorCautionBrush",
        PlanStatus.Error => "SystemFillColorCriticalBrush",
        _ => "TextFillColorSecondaryBrush",
    });

    public static Brush IssueForeground(bool isWarning) =>
        Brush(isWarning ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush");

    public static string IssueGlyph(bool isWarning) => isWarning ? "" : "";

    /// <summary>Opacità ridotta per le righe che non verranno toccate (errori, già corretti).</summary>
    public static double RowOpacity(PlanStatus status) => status is PlanStatus.Error or PlanStatus.Unchanged or PlanStatus.Skipped ? 0.6 : 1.0;

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
