using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Renamr.Core.BatchRename;
using Renamr.Core.Localization;

namespace Renamr.Presentation.ViewModels;

/// <summary>Una voce del menu "Aggiungi regola". <see cref="Label"/> è nella lingua dell'interfaccia.</summary>
public sealed record BatchRuleKind(string Key, string LabelKey, Func<BatchRuleViewModel> Create)
{
    public string Label => Strings.Current[LabelKey];
}

/// <summary>
/// Pannello delle regole della modalità "Rinomina file": quali file, in che ordine, e l'elenco di regole applicate
/// in sequenza. Non conosce il disco: segnala soltanto che qualcosa è cambiato, e la finestra ricalcola l'anteprima.
/// </summary>
public sealed partial class BatchRenameViewModel : ObservableObject
{
    private bool _loading;

    public BatchRenameViewModel(BatchRenameOptions? options = null) => Load(options ?? BatchRenameOptions.Default);

    /// <summary>Le regole sono cambiate: basta ricalcolare i nomi.</summary>
    public event EventHandler? RulesChanged;

    /// <summary>Sono cambiati filtro o sottocartelle: bisogna rileggere la cartella.</summary>
    public event EventHandler? ScopeChanged;

    public ObservableCollection<BatchRuleViewModel> Rules { get; } = [];

    public static IReadOnlyList<BatchRuleKind> RuleKinds { get; } =
    [
        new("numerazione", nameof(Strings.RuleNumbering), () => new NumberingRuleViewModel()),
        new("nuovoNome", nameof(Strings.RuleNewNameMenu), () => new NewNameRuleViewModel { Pattern = $"{{{Strings.Current.TokenName}}}" }),
        new("sostituisci", nameof(Strings.RuleReplace), () => new ReplaceTextRuleViewModel()),
        new("inserisci", nameof(Strings.RuleInsert), () => new InsertTextRuleViewModel()),
        new("rimuovi", nameof(Strings.RuleRemoveChars), () => new RemoveCharactersRuleViewModel()),
        new("maiuscole", nameof(Strings.RuleCase), () => new ChangeCaseRuleViewModel()),
        new("pulisci", nameof(Strings.RuleCleanup), () => new CleanupRuleViewModel()),
        new("lettereInNumeri", nameof(Strings.RuleLettersToDigits), () => new LettersToDigitsRuleViewModel()),
        new("estensione", nameof(Strings.RuleExtension), () => new ExtensionRuleViewModel()),
    ];

    /// <summary>Nella lingua dell'interfaccia: la finestra le rilegge quando si ricrea dopo un cambio di lingua.</summary>
    public static IReadOnlyList<string> SortOptions =>
    [
        Strings.Current.SortName, Strings.Current.SortModified, Strings.Current.SortCreated, Strings.Current.SortSize, Strings.Current.SortExtension,
    ];

    [ObservableProperty]
    public partial int SortByIndex { get; set; }

    [ObservableProperty]
    public partial bool Descending { get; set; }

    [ObservableProperty]
    public partial bool IncludeSubfolders { get; set; }

    /// <summary>"*.jpg; *.png", "jpg png", "IMG_*". Vuoto = tutti i file.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    public bool HasRules => Rules.Count > 0;

    /// <summary>Lingua dell'interfaccia cambiata: titoli e descrizioni delle schede.</summary>
    public void RefreshTexts()
    {
        foreach (var rule in Rules)
        {
            rule.RefreshTexts();
        }
    }

    public BatchRenameOptions ToOptions() => new()
    {
        Rules = [.. Rules.Select(r => r.ToRule())],
        SortBy = (BatchSortBy)Math.Clamp(SortByIndex, 0, SortOptions.Count - 1),
        Descending = Descending,
        IncludeSubfolders = IncludeSubfolders,
        Filter = Filter ?? string.Empty,
    };

    /// <summary>Aggiunge in fondo una regola del tipo scelto (chiave di <see cref="RuleKinds"/>).</summary>
    [RelayCommand]
    private void AddRule(string? key)
    {
        if (RuleKinds.FirstOrDefault(k => k.Key == key) is { } kind)
        {
            Insert(Rules.Count, kind.Create());
            Renumber();
            NotifyRulesChanged();
        }
    }

    /// <summary>Svuota l'elenco: i nomi tornano quelli originali.</summary>
    [RelayCommand]
    private void ClearRules()
    {
        Rules.Clear();
        Renumber();
        NotifyRulesChanged();
    }

    internal void Move(BatchRuleViewModel rule, int delta)
    {
        var from = Rules.IndexOf(rule);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Rules.Count)
        {
            return;
        }
        Rules.Move(from, to);
        Renumber();
        NotifyRulesChanged();
    }

    internal void RemoveRule(BatchRuleViewModel rule)
    {
        if (Rules.Remove(rule))
        {
            Renumber();
            NotifyRulesChanged();
        }
    }

    internal void NotifyRulesChanged()
    {
        if (!_loading)
        {
            RulesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnSortByIndexChanged(int value) => NotifyRulesChanged();

    partial void OnDescendingChanged(bool value) => NotifyRulesChanged();

    partial void OnIncludeSubfoldersChanged(bool value) => NotifyScopeChanged();

    partial void OnFilterChanged(string value) => NotifyScopeChanged();

    private void NotifyScopeChanged()
    {
        if (!_loading)
        {
            ScopeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Load(BatchRenameOptions options)
    {
        _loading = true;
        try
        {
            SortByIndex = (int)options.SortBy;
            Descending = options.Descending;
            IncludeSubfolders = options.IncludeSubfolders;
            Filter = options.Filter;
            foreach (var rule in options.Rules)
            {
                Insert(Rules.Count, BatchRuleViewModel.Create(rule));
            }
            Renumber();
        }
        finally
        {
            _loading = false;
        }
    }

    private void Insert(int index, BatchRuleViewModel rule)
    {
        rule.Attach(this);
        Rules.Insert(index, rule);
    }

    private void Renumber()
    {
        for (var i = 0; i < Rules.Count; i++)
        {
            Rules[i].SetPosition(i, Rules.Count);
        }
        OnPropertyChanged(nameof(HasRules));
    }
}
