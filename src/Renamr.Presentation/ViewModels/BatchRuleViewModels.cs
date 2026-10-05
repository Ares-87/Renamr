using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Renamr.Core.BatchRename;
using Renamr.Core.Localization;

namespace Renamr.Presentation.ViewModels;

/// <summary>
/// Una "scheda" nell'elenco delle regole della modalità "Rinomina file". Ogni modifica avvisa il proprietario,
/// che ricalcola l'anteprima. Le scelte da menu a tendina sono indici (0, 1, 2…) così si legano allo stesso modo
/// in WinUI e in Avalonia; i numeri sono double per NumberBox/NumericUpDown.
/// </summary>
public abstract partial class BatchRuleViewModel : ObservableObject
{
    private BatchRenameViewModel? _owner;

    protected BatchRuleViewModel() => PropertyChanged += OnAnyPropertyChanged;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    private int _index;

    /// <summary>"1. Numerazione": il numero dice l'ordine in cui le regole si applicano.</summary>
    public string Header => $"{_index + 1}. {Title}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    public partial bool CanMoveUp { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    public partial bool CanMoveDown { get; private set; }

    public abstract string Title { get; }

    /// <summary>Una riga di aiuto sotto il titolo.</summary>
    public abstract string Description { get; }

    public abstract BatchRule ToRule();

    internal void Attach(BatchRenameViewModel owner) => _owner = owner;

    /// <summary>Il pannello che contiene la regola (null finché non è nell'elenco).</summary>
    protected BatchRenameViewModel? Owner => _owner;

    internal void SetPosition(int index, int count)
    {
        _index = index;
        OnPropertyChanged(nameof(Header));
        CanMoveUp = index > 0;
        CanMoveDown = index < count - 1;
    }

    /// <summary>Lingua dell'interfaccia cambiata.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Description));
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => _owner?.Move(this, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => _owner?.Move(this, +1);

    [RelayCommand]
    private void Remove() => _owner?.RemoveRule(this);

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Header) or nameof(Description) or nameof(CanMoveUp) or nameof(CanMoveDown) or "Error" or "HasError"))
        {
            OnRuleChanged();
            _owner?.NotifyRulesChanged();
        }
    }

    /// <summary>Per le regole che validano mentre si scrive.</summary>
    protected virtual void OnRuleChanged()
    {
    }

    public static BatchRuleViewModel Create(BatchRule rule)
    {
        BatchRuleViewModel vm = rule switch
        {
            NewNameRule r => new NewNameRuleViewModel { Pattern = r.Pattern },
            NumberingRule r => new NumberingRuleViewModel
            {
                PositionIndex = (int)r.Position,
                Start = r.Start,
                Step = r.Step,
                Digits = r.Digits,
                Separator = r.Separator,
                RestartInEachFolder = r.RestartInEachFolder,
            },
            ReplaceTextRule r => new ReplaceTextRuleViewModel
            {
                Find = r.Find,
                Replacement = r.Replacement,
                MatchCase = r.MatchCase,
                UseRegex = r.UseRegex,
                OccurrenceIndex = (int)r.Occurrence,
            },
            InsertTextRule r => new InsertTextRuleViewModel { Text = r.Text, PositionIndex = (int)r.Position, Index = r.Index, Anchor = r.Anchor },
            RemoveCharactersRule r => new RemoveCharactersRuleViewModel { ModeIndex = (int)r.Mode, Count = r.Count, From = r.From, Characters = r.Characters },
            ChangeCaseRule r => new ChangeCaseRuleViewModel { ModeIndex = (int)r.Mode },
            CleanupRule r => new CleanupRuleViewModel
            {
                SeparatorsToSpaces = r.SeparatorsToSpaces,
                RemoveBracketed = r.RemoveBracketed,
                RemoveAccents = r.RemoveAccents,
                RemoveDigits = r.RemoveDigits,
            },
            LettersToDigitsRule r => new LettersToDigitsRuleViewModel { A = r.A, E = r.E, I = r.I, O = r.O, S = r.S, T = r.T, B = r.B, G = r.G },
            ExtensionRule r => new ExtensionRuleViewModel { ModeIndex = (int)r.Mode, NewExtension = r.NewExtension },
            MoveTextRule r => new MoveTextRuleViewModel
            {
                SourceIndex = (int)r.Source,
                From = r.From,
                Count = r.Count,
                Find = r.Find,
                MatchCase = r.MatchCase,
                UseRegex = r.UseRegex,
                TargetIndex = (int)r.Target,
                TargetPosition = r.TargetIndex,
                Separator = r.Separator,
            },
            SwapPartsRule r => new SwapPartsRuleViewModel { Separator = r.Separator, Occurrence = r.Occurrence, FromEnd = r.FromEnd },
            RenumberRule r => new RenumberRuleViewModel
            {
                Which = r.Which,
                FromEnd = r.FromEnd,
                ModeIndex = (int)r.Mode,
                Start = r.Start,
                Step = r.Step,
                Add = r.Add,
                Digits = r.Digits,
            },
            TrimRule r => new TrimRuleViewModel { Characters = r.Characters, WhereIndex = (int)r.Where },
            NameListRule r => new NameListRuleViewModel { Names = r.Names },
            ReplaceListRule r => ReplaceListRuleViewModel.From(r),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Regola sconosciuta"),
        };
        vm.Enabled = rule.Enabled;
        return vm;
    }

    protected static int ToInt(double value, int min, int max) =>
        double.IsNaN(value) ? min : (int)Math.Clamp(Math.Round(value), min, max);
}

public sealed partial class NewNameRuleViewModel : BatchRuleViewModel
{
    [ObservableProperty]
    public partial string Pattern { get; set; } = "{nome}";

    public override string Title => Strings.Current.RuleNewName;

    public override string Description => Strings.Current.RuleNewNameDesc;

    public static string TokensHelp => string.Join(Environment.NewLine, BatchTokens.Help.Select(t => $"{t.Token}  {t.Description}"));

    /// <summary>Parti del nome, dimensione e dati letti da foto, musica e video: in un riquadro che si apre a richiesta.</summary>
    public static string MoreTokensHelp => string.Join(Environment.NewLine, BatchTokens.MoreHelp.Select(t => $"{t.Token}  {t.Description}"));

    public override BatchRule ToRule() => new NewNameRule { Enabled = Enabled, Pattern = Pattern ?? string.Empty };
}

public sealed partial class NumberingRuleViewModel : BatchRuleViewModel
{
    public static IReadOnlyList<string> Positions => [Strings.Current.PositionStart, Strings.Current.PositionEnd, Strings.Current.PositionReplaceName];

    [ObservableProperty]
    public partial int PositionIndex { get; set; } = (int)NumberPosition.End;

    // Una ComboBox senza voci (WinUI, mentre si crea il modello) rimanda -1: si tiene la scelta di prima.
    partial void OnPositionIndexChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            PositionIndex = oldValue;
        }
    }

    [ObservableProperty]
    public partial double Start { get; set; } = 1;

    [ObservableProperty]
    public partial double Step { get; set; } = 1;

    /// <summary>0 = automatiche.</summary>
    [ObservableProperty]
    public partial double Digits { get; set; }

    [ObservableProperty]
    public partial string Separator { get; set; } = " ";

    [ObservableProperty]
    public partial bool RestartInEachFolder { get; set; }

    public override string Title => Strings.Current.RuleNumbering;

    public override string Description => Strings.Current.RuleNumberingDesc;

    public override BatchRule ToRule() => new NumberingRule
    {
        Enabled = Enabled,
        Position = (NumberPosition)Math.Clamp(PositionIndex, 0, 2),
        Start = ToInt(Start, -1_000_000, 1_000_000_000),
        Step = ToInt(Step, -1_000_000, 1_000_000),
        Digits = ToInt(Digits, 0, 12),
        Separator = Separator ?? string.Empty,
        RestartInEachFolder = RestartInEachFolder,
    };
}

public sealed partial class ReplaceTextRuleViewModel : BatchRuleViewModel
{
    [ObservableProperty]
    public partial string Find { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Replacement { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    public static IReadOnlyList<string> Occurrences => [Strings.Current.ReplaceAll, Strings.Current.ReplaceFirst, Strings.Current.ReplaceLast];

    [ObservableProperty]
    public partial int OccurrenceIndex { get; set; }

    // Una ComboBox senza voci (WinUI, mentre si crea il modello) rimanda -1: si tiene la scelta di prima.
    partial void OnOccurrenceIndexChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            OccurrenceIndex = oldValue;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public override string Title => Strings.Current.RuleReplace;

    public override string Description => Strings.Current.RuleReplaceDesc;

    public override BatchRule ToRule() => new ReplaceTextRule
    {
        Enabled = Enabled,
        Find = Find ?? string.Empty,
        Replacement = Replacement ?? string.Empty,
        MatchCase = MatchCase,
        UseRegex = UseRegex,
        Occurrence = (ReplaceOccurrence)Math.Clamp(OccurrenceIndex, 0, 2),
    };

    protected override void OnRuleChanged() => Error = ((ReplaceTextRule)ToRule()).Validate();
}

public sealed partial class InsertTextRuleViewModel : BatchRuleViewModel
{
    public static IReadOnlyList<string> Positions =>
    [
        Strings.Current.PositionStart, Strings.Current.PositionEnd, Strings.Current.PositionAfterChars,
        Strings.Current.PositionBeforeLastChars, Strings.Current.PositionBeforeText, Strings.Current.PositionAfterText,
    ];

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAtIndex))]
    [NotifyPropertyChangedFor(nameof(IsAtText))]
    public partial int PositionIndex { get; set; }

    // Una ComboBox senza voci (WinUI, mentre si crea il modello) rimanda -1: si tiene la scelta di prima.
    partial void OnPositionIndexChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            PositionIndex = oldValue;
        }
    }

    [ObservableProperty]
    public partial double Index { get; set; }

    [ObservableProperty]
    public partial string Anchor { get; set; } = string.Empty;

    public bool IsAtIndex => PositionIndex is (int)InsertPosition.AfterCharacters or (int)InsertPosition.BeforeLastCharacters;

    public bool IsAtText => PositionIndex is (int)InsertPosition.BeforeText or (int)InsertPosition.AfterText;

    public override string Title => Strings.Current.RuleInsert;

    public override string Description => Strings.Current.RuleInsertDesc;

    public override BatchRule ToRule() => new InsertTextRule
    {
        Enabled = Enabled,
        Text = Text ?? string.Empty,
        Position = (InsertPosition)Math.Clamp(PositionIndex, 0, 5),
        Index = ToInt(Index, 0, 1000),
        Anchor = Anchor ?? string.Empty,
    };
}

public sealed partial class RemoveCharactersRuleViewModel : BatchRuleViewModel
{
    public static IReadOnlyList<string> Modes =>
    [
        Strings.Current.RemoveFirst, Strings.Current.RemoveLast, Strings.Current.RemoveRange, Strings.Current.RemoveCharacterList,
        Strings.Current.RemoveSymbols, Strings.Current.RemoveAllDigits, Strings.Current.RemoveLetters, Strings.Current.RemoveAllButDigits,
        Strings.Current.RemoveUppercase, Strings.Current.RemoveLowercase,
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRange))]
    [NotifyPropertyChangedFor(nameof(IsCount))]
    [NotifyPropertyChangedFor(nameof(IsCharacterList))]
    public partial int ModeIndex { get; set; }

    // Una ComboBox senza voci (WinUI, mentre si crea il modello) rimanda -1: si tiene la scelta di prima.
    partial void OnModeIndexChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            ModeIndex = oldValue;
        }
    }

    [ObservableProperty]
    public partial double Count { get; set; } = 1;

    [ObservableProperty]
    public partial double From { get; set; } = 1;

    [ObservableProperty]
    public partial string Characters { get; set; } = string.Empty;

    public bool IsRange => ModeIndex == (int)RemoveMode.Range;

    /// <summary>Le modalità per posizione chiedono quanti caratteri; quelle per tipo no.</summary>
    public bool IsCount => ModeIndex <= (int)RemoveMode.Range;

    public bool IsCharacterList => ModeIndex == (int)RemoveMode.CharacterList;

    public override string Title => Strings.Current.RuleRemoveChars;

    public override string Description => Strings.Current.RuleRemoveCharsDesc;

    public override BatchRule ToRule() => new RemoveCharactersRule
    {
        Enabled = Enabled,
        Mode = (RemoveMode)Math.Clamp(ModeIndex, 0, (int)RemoveMode.Lowercase),
        Count = ToInt(Count, 0, 1000),
        From = ToInt(From, 1, 1000),
        Characters = Characters ?? string.Empty,
    };
}

public sealed partial class ChangeCaseRuleViewModel : BatchRuleViewModel
{
    public static IReadOnlyList<string> Modes => [Strings.Current.CaseLower, Strings.Current.CaseUpper, Strings.Current.CaseTitle, Strings.Current.CaseSentence, Strings.Current.CaseInvert];

    [ObservableProperty]
    public partial int ModeIndex { get; set; } = (int)CaseMode.TitleCase;

    // Una ComboBox senza voci (WinUI, mentre si crea il modello) rimanda -1: si tiene la scelta di prima.
    partial void OnModeIndexChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            ModeIndex = oldValue;
        }
    }

    public override string Title => Strings.Current.RuleCase;

    public override string Description => Strings.Current.RuleCaseDesc;

    public override BatchRule ToRule() => new ChangeCaseRule { Enabled = Enabled, Mode = (CaseMode)Math.Clamp(ModeIndex, 0, (int)CaseMode.Invert) };
}

public sealed partial class CleanupRuleViewModel : BatchRuleViewModel
{
    [ObservableProperty]
    public partial bool SeparatorsToSpaces { get; set; } = true;

    [ObservableProperty]
    public partial bool RemoveBracketed { get; set; }

    [ObservableProperty]
    public partial bool RemoveAccents { get; set; }

    [ObservableProperty]
    public partial bool RemoveDigits { get; set; }

    public override string Title => Strings.Current.RuleCleanup;

    public override string Description => Strings.Current.RuleCleanupDesc;

    public override BatchRule ToRule() => new CleanupRule
    {
        Enabled = Enabled,
        SeparatorsToSpaces = SeparatorsToSpaces,
        RemoveBracketed = RemoveBracketed,
        RemoveAccents = RemoveAccents,
        RemoveDigits = RemoveDigits,
    };
}

/// <summary>Una casella per lettera: "A ➔ 4", "E ➔ 3"… (le etichette sono uguali in tutte le lingue).</summary>
public sealed partial class LettersToDigitsRuleViewModel : BatchRuleViewModel
{
    [ObservableProperty]
    public partial bool A { get; set; } = true;

    [ObservableProperty]
    public partial bool E { get; set; } = true;

    [ObservableProperty]
    public partial bool I { get; set; } = true;

    [ObservableProperty]
    public partial bool O { get; set; } = true;

    [ObservableProperty]
    public partial bool S { get; set; } = true;

    [ObservableProperty]
    public partial bool T { get; set; } = true;

    [ObservableProperty]
    public partial bool B { get; set; }

    [ObservableProperty]
    public partial bool G { get; set; }

    public override string Title => Strings.Current.RuleLettersToDigits;

    public override string Description => Strings.Current.RuleLettersToDigitsDesc;

    public override BatchRule ToRule() => new LettersToDigitsRule { Enabled = Enabled, A = A, E = E, I = I, O = O, S = S, T = T, B = B, G = G };
}

public sealed partial class ExtensionRuleViewModel : BatchRuleViewModel
{
    public static IReadOnlyList<string> Modes => [Strings.Current.ExtensionLower, Strings.Current.ExtensionUpper, Strings.Current.ExtensionReplace];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReplace))]
    public partial int ModeIndex { get; set; }

    // Una ComboBox senza voci (WinUI, mentre si crea il modello) rimanda -1: si tiene la scelta di prima.
    partial void OnModeIndexChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            ModeIndex = oldValue;
        }
    }

    [ObservableProperty]
    public partial string NewExtension { get; set; } = string.Empty;

    public bool IsReplace => ModeIndex == (int)ExtensionMode.Replace;

    public override string Title => Strings.Current.RuleExtension;

    public override string Description => Strings.Current.RuleExtensionDesc;

    public override BatchRule ToRule() => new ExtensionRule
    {
        Enabled = Enabled,
        Mode = (ExtensionMode)Math.Clamp(ModeIndex, 0, 2),
        NewExtension = NewExtension ?? string.Empty,
    };
}

public sealed partial class MoveTextRuleViewModel : BatchRuleViewModel
{
    public static IReadOnlyList<string> Sources => [Strings.Current.MoveSourceChars, Strings.Current.MoveSourceText];

    public static IReadOnlyList<string> Targets => [Strings.Current.PositionStart, Strings.Current.PositionEnd, Strings.Current.PositionAfterChars];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText))]
    [NotifyPropertyChangedFor(nameof(IsCharacters))]
    public partial int SourceIndex { get; set; } = (int)MoveSource.Text;

    // Una ComboBox senza voci (WinUI, mentre si crea il modello) rimanda -1: si tiene la scelta di prima.
    partial void OnSourceIndexChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            SourceIndex = oldValue;
        }
    }

    [ObservableProperty]
    public partial double From { get; set; } = 1;

    [ObservableProperty]
    public partial double Count { get; set; } = 1;

    [ObservableProperty]
    public partial string Find { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAtIndex))]
    public partial int TargetIndex { get; set; } = (int)MoveTarget.End;

    partial void OnTargetIndexChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            TargetIndex = oldValue;
        }
    }

    /// <summary>Per "Dopo il carattere…": quanti caratteri del resto del nome lasciare prima.</summary>
    [ObservableProperty]
    public partial double TargetPosition { get; set; }

    [ObservableProperty]
    public partial string Separator { get; set; } = " ";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public bool IsText => SourceIndex == (int)MoveSource.Text;

    public bool IsCharacters => SourceIndex == (int)MoveSource.Characters;

    public bool IsAtIndex => TargetIndex == (int)MoveTarget.AfterCharacters;

    public override string Title => Strings.Current.RuleMove;

    public override string Description => Strings.Current.RuleMoveDesc;

    public override BatchRule ToRule() => new MoveTextRule
    {
        Enabled = Enabled,
        Source = (MoveSource)Math.Clamp(SourceIndex, 0, 1),
        From = ToInt(From, 1, 1000),
        Count = ToInt(Count, 0, 1000),
        Find = Find ?? string.Empty,
        MatchCase = MatchCase,
        UseRegex = UseRegex,
        Target = (MoveTarget)Math.Clamp(TargetIndex, 0, 2),
        TargetIndex = ToInt(TargetPosition, 0, 1000),
        Separator = Separator ?? string.Empty,
    };

    protected override void OnRuleChanged() => Error = ((MoveTextRule)ToRule()).Validate();
}

public sealed partial class SwapPartsRuleViewModel : BatchRuleViewModel
{
    [ObservableProperty]
    public partial string Separator { get; set; } = " - ";

    [ObservableProperty]
    public partial double Occurrence { get; set; } = 1;

    [ObservableProperty]
    public partial bool FromEnd { get; set; }

    public override string Title => Strings.Current.RuleSwap;

    public override string Description => Strings.Current.RuleSwapDesc;

    public override BatchRule ToRule() => new SwapPartsRule
    {
        Enabled = Enabled,
        Separator = Separator ?? string.Empty,
        Occurrence = ToInt(Occurrence, 1, 100),
        FromEnd = FromEnd,
    };
}

public sealed partial class RenumberRuleViewModel : BatchRuleViewModel
{
    public static IReadOnlyList<string> Modes => [Strings.Current.RenumberSequence, Strings.Current.RenumberAddMode];

    [ObservableProperty]
    public partial double Which { get; set; } = 1;

    [ObservableProperty]
    public partial bool FromEnd { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSequence))]
    [NotifyPropertyChangedFor(nameof(IsAdd))]
    public partial int ModeIndex { get; set; }

    // Una ComboBox senza voci (WinUI, mentre si crea il modello) rimanda -1: si tiene la scelta di prima.
    partial void OnModeIndexChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            ModeIndex = oldValue;
        }
    }

    [ObservableProperty]
    public partial double Start { get; set; } = 1;

    [ObservableProperty]
    public partial double Step { get; set; } = 1;

    [ObservableProperty]
    public partial double Add { get; set; } = 1;

    /// <summary>0 = come il numero originale.</summary>
    [ObservableProperty]
    public partial double Digits { get; set; }

    public bool IsSequence => ModeIndex == (int)RenumberMode.Sequence;

    public bool IsAdd => ModeIndex == (int)RenumberMode.Add;

    public override string Title => Strings.Current.RuleRenumber;

    public override string Description => Strings.Current.RuleRenumberDesc;

    public override BatchRule ToRule() => new RenumberRule
    {
        Enabled = Enabled,
        Which = ToInt(Which, 1, 100),
        FromEnd = FromEnd,
        Mode = (RenumberMode)Math.Clamp(ModeIndex, 0, 1),
        Start = ToInt(Start, -1_000_000, 1_000_000_000),
        Step = ToInt(Step, -1_000_000, 1_000_000),
        Add = ToInt(Add, -1_000_000_000, 1_000_000_000),
        Digits = ToInt(Digits, 0, 12),
    };
}

public sealed partial class TrimRuleViewModel : BatchRuleViewModel
{
    public static IReadOnlyList<string> Places => [Strings.Current.TrimBoth, Strings.Current.TrimStart, Strings.Current.TrimEnd];

    [ObservableProperty]
    public partial string Characters { get; set; } = " .-_";

    [ObservableProperty]
    public partial int WhereIndex { get; set; }

    // Una ComboBox senza voci (WinUI, mentre si crea il modello) rimanda -1: si tiene la scelta di prima.
    partial void OnWhereIndexChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            WhereIndex = oldValue;
        }
    }

    public override string Title => Strings.Current.RuleTrim;

    public override string Description => Strings.Current.RuleTrimDesc;

    public override BatchRule ToRule() => new TrimRule
    {
        Enabled = Enabled,
        Characters = Characters ?? string.Empty,
        Where = (TrimWhere)Math.Clamp(WhereIndex, 0, 2),
    };
}

public sealed partial class NameListRuleViewModel : BatchRuleViewModel
{
    [ObservableProperty]
    public partial string Names { get; set; } = string.Empty;

    public override string Title => Strings.Current.RuleNameList;

    public override string Description => Strings.Current.RuleNameListDesc;

    public override BatchRule ToRule() => new NameListRule { Enabled = Enabled, Names = Names ?? string.Empty };

    /// <summary>Scrive nel riquadro i nomi dei file come sono ora, nell'ordine scelto, da ritoccare a mano.</summary>
    [RelayCommand]
    private void FillWithCurrentNames()
    {
        if (Owner?.CurrentNames?.Invoke() is { } names)
        {
            Names = NameListRule.FromNames(names);
        }
    }
}

/// <summary>Una riga "cerca ➔ sostituisci con" di <see cref="ReplaceListRuleViewModel"/>.</summary>
public sealed partial class ReplacePairViewModel : ObservableObject
{
    private readonly ReplaceListRuleViewModel _rule;

    internal ReplacePairViewModel(ReplaceListRuleViewModel rule, string find = "", string replacement = "")
    {
        _rule = rule;
        Find = find;
        Replacement = replacement;
        PropertyChanged += (_, _) => _rule.PairChanged();
    }

    [ObservableProperty]
    public partial string Find { get; set; }

    [ObservableProperty]
    public partial string Replacement { get; set; }

    [RelayCommand]
    private void Remove() => _rule.RemovePair(this);
}

public sealed partial class ReplaceListRuleViewModel : BatchRuleViewModel
{
    public ReplaceListRuleViewModel() => Pairs.Add(new ReplacePairViewModel(this));

    public System.Collections.ObjectModel.ObservableCollection<ReplacePairViewModel> Pairs { get; } = [];

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public override string Title => Strings.Current.RuleReplaceList;

    public override string Description => Strings.Current.RuleReplaceListDesc;

    public static ReplaceListRuleViewModel From(ReplaceListRule rule)
    {
        var vm = new ReplaceListRuleViewModel { MatchCase = rule.MatchCase, UseRegex = rule.UseRegex };
        if (rule.Pairs.Count > 0)
        {
            vm.Pairs.Clear();
            foreach (var pair in rule.Pairs)
            {
                vm.Pairs.Add(new ReplacePairViewModel(vm, pair.Find, pair.Replacement));
            }
        }
        return vm;
    }

    public override BatchRule ToRule() => new ReplaceListRule
    {
        Enabled = Enabled,
        Pairs = [.. Pairs.Select(p => new ReplacePair(p.Find ?? string.Empty, p.Replacement ?? string.Empty))],
        MatchCase = MatchCase,
        UseRegex = UseRegex,
    };

    [RelayCommand]
    private void AddPair()
    {
        Pairs.Add(new ReplacePairViewModel(this));
        PairChanged();
    }

    internal void RemovePair(ReplacePairViewModel pair)
    {
        if (Pairs.Remove(pair))
        {
            PairChanged();
        }
    }

    internal void PairChanged()
    {
        OnRuleChanged();
        Owner?.NotifyRulesChanged();
    }

    protected override void OnRuleChanged() => Error = ((ReplaceListRule)ToRule()).Validate();
}
