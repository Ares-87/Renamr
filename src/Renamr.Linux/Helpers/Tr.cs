using Avalonia.Markup.Xaml;
using Renamr.Core.Localization;

namespace Renamr.Linux.Helpers;

/// <summary>
/// <c>{l:Tr ModeMedia}</c>: il testo nella lingua dell'interfaccia (chiavi in Renamr.Core/Localization/*.json).
/// Si legge una volta: dopo un cambio di lingua la finestra si ricrea. Il test LocalizationTests controlla che ogni
/// chiave usata negli .axaml esista.
/// </summary>
public sealed class Tr(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Current[Key];
}
