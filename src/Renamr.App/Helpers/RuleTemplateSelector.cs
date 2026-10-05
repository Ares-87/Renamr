using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Renamr.Presentation.ViewModels;

namespace Renamr.App.Helpers;

/// <summary>Sceglie la scheda giusta per ogni regola della modalità "Rinomina file" (in Avalonia lo fa il DataType).</summary>
public sealed partial class RuleTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Numbering { get; set; }
    public DataTemplate? NewName { get; set; }
    public DataTemplate? ReplaceText { get; set; }
    public DataTemplate? InsertText { get; set; }
    public DataTemplate? RemoveCharacters { get; set; }
    public DataTemplate? ChangeCase { get; set; }
    public DataTemplate? Cleanup { get; set; }
    public DataTemplate? LettersToDigits { get; set; }
    public DataTemplate? Extension { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        NumberingRuleViewModel => Numbering,
        NewNameRuleViewModel => NewName,
        ReplaceTextRuleViewModel => ReplaceText,
        InsertTextRuleViewModel => InsertText,
        RemoveCharactersRuleViewModel => RemoveCharacters,
        ChangeCaseRuleViewModel => ChangeCase,
        CleanupRuleViewModel => Cleanup,
        LettersToDigitsRuleViewModel => LettersToDigits,
        ExtensionRuleViewModel => Extension,
        _ => null,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
