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
            ReplaceTextRule r => new ReplaceTextRuleViewModel { Find = r.Find, Replacement = r.Replacement, MatchCase = r.MatchCase, UseRegex = r.UseRegex },
            InsertTextRule r => new InsertTextRuleViewModel { Text = r.Text, PositionIndex = (int)r.Position, Index = r.Index },
            RemoveCharactersRule r => new RemoveCharactersRuleViewModel { ModeIndex = (int)r.Mode, Count = r.Count, From = r.From },
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

    public override BatchRule ToRule() => new NewNameRule { Enabled = Enabled, Pattern = Pattern ?? string.Empty };
}

public sealed partial class NumberingRuleViewModel : BatchRuleViewModel
{
    public static IReadOnlyList<string> Positions => [Strings.Current.PositionStart, Strings.Current.PositionEnd, Strings.Current.PositionReplaceName];

    /// <summary>Le stesse voci per x:Bind nei DataTemplate di WinUI: una proprietà dell'istanza si aggiorna prima di
    /// SelectedIndex, una statica dopo, e la ComboBox restava vuota.</summary>
    public IReadOnlyList<string> PositionNames => Positions;

    [ObservableProperty]
    public partial int PositionIndex { get; set; } = (int)NumberPosition.End;

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
    };

    protected override void OnRuleChanged() => Error = ((ReplaceTextRule)ToRule()).Validate();
}

public sealed partial class InsertTextRuleViewModel : BatchRuleViewModel
{
    public static IReadOnlyList<string> Positions => [Strings.Current.PositionStart, Strings.Current.PositionEnd, Strings.Current.PositionAfterChars];

    /// <summary>Le stesse voci per x:Bind nei DataTemplate di WinUI: una proprietà dell'istanza si aggiorna prima di
    /// SelectedIndex, una statica dopo, e la ComboBox restava vuota.</summary>
    public IReadOnlyList<string> PositionNames => Positions;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAtIndex))]
    public partial int PositionIndex { get; set; }

    [ObservableProperty]
    public partial double Index { get; set; }

    public bool IsAtIndex => PositionIndex == (int)InsertPosition.AfterCharacters;

    public override string Title => Strings.Current.RuleInsert;

    public override string Description => Strings.Current.RuleInsertDesc;

    public override BatchRule ToRule() => new InsertTextRule
    {
        Enabled = Enabled,
        Text = Text ?? string.Empty,
        Position = (InsertPosition)Math.Clamp(PositionIndex, 0, 2),
        Index = ToInt(Index, 0, 1000),
    };
}

public sealed partial class RemoveCharactersRuleViewModel : BatchRuleViewModel
{
    public static IReadOnlyList<string> Modes => [Strings.Current.RemoveFirst, Strings.Current.RemoveLast, Strings.Current.RemoveRange];

    /// <summary>Le stesse voci per x:Bind nei DataTemplate di WinUI: una proprietà dell'istanza si aggiorna prima di
    /// SelectedIndex, una statica dopo, e la ComboBox restava vuota.</summary>
    public IReadOnlyList<string> ModeNames => Modes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRange))]
    public partial int ModeIndex { get; set; }

    [ObservableProperty]
    public partial double Count { get; set; } = 1;

    [ObservableProperty]
    public partial double From { get; set; } = 1;

    public bool IsRange => ModeIndex == (int)RemoveMode.Range;

    public override string Title => Strings.Current.RuleRemoveChars;

    public override string Description => Strings.Current.RuleRemoveCharsDesc;

    public override BatchRule ToRule() => new RemoveCharactersRule
    {
        Enabled = Enabled,
        Mode = (RemoveMode)Math.Clamp(ModeIndex, 0, 2),
        Count = ToInt(Count, 0, 1000),
        From = ToInt(From, 1, 1000),
    };
}

public sealed partial class ChangeCaseRuleViewModel : BatchRuleViewModel
{
    public static IReadOnlyList<string> Modes => [Strings.Current.CaseLower, Strings.Current.CaseUpper, Strings.Current.CaseTitle, Strings.Current.CaseSentence];

    /// <summary>Le stesse voci per x:Bind nei DataTemplate di WinUI: una proprietà dell'istanza si aggiorna prima di
    /// SelectedIndex, una statica dopo, e la ComboBox restava vuota.</summary>
    public IReadOnlyList<string> ModeNames => Modes;

    [ObservableProperty]
    public partial int ModeIndex { get; set; } = (int)CaseMode.TitleCase;

    public override string Title => Strings.Current.RuleCase;

    public override string Description => Strings.Current.RuleCaseDesc;

    public override BatchRule ToRule() => new ChangeCaseRule { Enabled = Enabled, Mode = (CaseMode)Math.Clamp(ModeIndex, 0, 3) };
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

    /// <summary>Le stesse voci per x:Bind nei DataTemplate di WinUI: una proprietà dell'istanza si aggiorna prima di
    /// SelectedIndex, una statica dopo, e la ComboBox restava vuota.</summary>
    public IReadOnlyList<string> ModeNames => Modes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReplace))]
    public partial int ModeIndex { get; set; }

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
