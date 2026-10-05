using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Renamr.Core.Localization;
using Renamr.Core.Models;
using Renamr.Core.Templating;
using Renamr.Presentation.Animation;
using Renamr.Presentation.Services;
using Renamr.Presentation.ViewModels;
using Renamr.Services.IO;

namespace Renamr.Linux.Views;

/// <summary>
/// Code-behind ridotto a ciò che è puramente "vista", come in Renamr.App: feedback del drag &amp; drop,
/// menu contestuale, apertura del dialog. Ogni azione vera passa dai comandi del ViewModel.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly MediaKind[] Kinds = [MediaKind.Movie, MediaKind.Episode, MediaKind.Anime, MediaKind.Music];

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        RootGrid.AddHandler(DragDrop.DragOverEvent, Root_DragOver);
        RootGrid.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHighlight(false));
        RootGrid.AddHandler(DragDrop.DropEvent, Root_Drop);
        FileList.ContextRequested += List_ContextRequested;
        FileList.Tapped += FileList_Tapped;
        IssueList.Tapped += Issue_Tapped;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateDateHint();

        UpdateModeTabs();

        // La tazzina del caffè: prima oscillazione poco dopo l'apertura, poi ogni tanto.
        _coffeeFrames = new DispatcherTimer { Interval = CoffeeWiggle.Frame };
        _coffeeFrames.Tick += (_, _) => CoffeeFrame();
        _coffeeTimer = new DispatcherTimer { Interval = CoffeeWiggle.FirstDelay };
        _coffeeTimer.Tick += (_, _) =>
        {
            _coffeeTimer.Interval = CoffeeWiggle.Interval;
            _coffeeStart = DateTime.UtcNow;
            _coffeeFrames.Start();
        };
        _coffeeTimer.Start();
    }

    private readonly DispatcherTimer _coffeeTimer;
    private readonly DispatcherTimer _coffeeFrames;
    private DateTime _coffeeStart;

    protected override void OnClosed(EventArgs e)
    {
        _coffeeTimer.Stop();
        _coffeeFrames.Stop();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnClosed(e);
    }

    public MainViewModel ViewModel { get; }

    // ---- Drag & drop: una cartella oppure dei file, ovunque nella finestra ------------------------------------

    private void Root_DragOver(object? sender, DragEventArgs e)
    {
        if (ViewModel.IsBusy || !e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Link;
        SetDropHighlight(true);
    }

    private async void Root_Drop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        var items = e.DataTransfer.TryGetFiles();
        if (items is null || items.Length == 0)
        {
            return;
        }
        // Una cartella apre quella cartella; dei file si aggiungono all'elenco (o ne fanno uno nuovo dalla schermata iniziale).
        var folder = items.OfType<IStorageFolder>().Select(f => f.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        var files = items.OfType<IStorageFile>().Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (folder is not null)
        {
            await ViewModel.OpenFolderCommand.ExecuteAsync(folder);
        }
        if (files.Count > 0)
        {
            await ViewModel.AddFilesCommand.ExecuteAsync(files);
        }
    }

    private void SetDropHighlight(bool active)
    {
        DropZoneOutline.Classes.Set("active", active);
        DropZoneTitle.Text = active ? Strings.Current.DropRelease : ViewModel.DropZoneTitle;
    }

    // ---- Date: su Linux la data di creazione si cambia solo su alcuni dischi ---------------------------------

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.PrimaryFolder) or nameof(MainViewModel.IsBatchMode) or nameof(MainViewModel.SetsCreationDate))
        {
            UpdateDateHint();
        }
        if (e.PropertyName is nameof(MainViewModel.IsBatchMode))
        {
            UpdateModeTabs();
        }
    }

    private (string? Folder, bool Batch, bool Creation)? _dateHintKey;

    private void UpdateDateHint()
    {
        // In modalità "Rinomina file" le date non si toccano, e se la data di creazione è spenta nelle Impostazioni
        // nemmeno: l'avviso non serve.
        var root = ViewModel.PrimaryFolder;
        // Aggiungere o togliere file non cambia il disco: l'avviso chiuso dall'utente resta chiuso.
        var key = (root, ViewModel.IsBatchMode, ViewModel.SetsCreationDate);
        if (key == _dateHintKey)
        {
            return;
        }
        _dateHintKey = key;
        if (root is null || ViewModel.IsBatchMode || !ViewModel.SetsCreationDate || FileCreationTime.CanSet(root))
        {
            DateHintBar.IsOpen = false;
            return;
        }
        DateHintBar.Message = Strings.Current.CreationDateHint;
        DateHintBar.IsOpen = true;
    }

    // ---- Schede della modalità e caffè ---------------------------------------------------------------------------

    private void MediaTab_Click(object? sender, RoutedEventArgs e) => ViewModel.ModeIndex = 0;

    private void BatchTab_Click(object? sender, RoutedEventArgs e) => ViewModel.ModeIndex = 1;

    private void UpdateModeTabs()
    {
        // La scheda scelta si riempie del suo colore (stili "selected" nell'axaml).
        MediaTab.Classes.Set("selected", ViewModel.IsMediaMode);
        BatchTab.Classes.Set("selected", ViewModel.IsBatchMode);
    }

    private void CoffeeFrame()
    {
        var elapsed = DateTime.UtcNow - _coffeeStart;
        var (angle, rise, opacity) = CoffeeWiggle.At(elapsed);
        ((RotateTransform)CoffeeCup.RenderTransform!).Angle = angle;
        ((TranslateTransform)CoffeeSteam.RenderTransform!).Y = -rise;
        CoffeeSteam.Opacity = opacity;
        if (elapsed >= CoffeeWiggle.Duration)
        {
            _coffeeFrames.Stop();
        }
    }

    private async void Coffee_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new FAContentDialog
        {
            Title = Strings.Current.DonateTitle,
            Content = new StackPanel
            {
                Spacing = 12,
                MaxWidth = 420,
                Children =
                {
                    new TextBlock { Text = Strings.Current.DonateText, TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = Strings.Current.DonateNote,
                        TextWrapping = TextWrapping.Wrap,
                        Classes = { "caption" },
                        Foreground = this.FindResource("TextFillColorSecondaryBrush") as IBrush,
                    },
                },
            },
            PrimaryButtonText = Strings.Current.DonateButton,
            CloseButtonText = Strings.Current.DonateLater,
            DefaultButton = FAContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync(this) == FAContentDialogResult.Primary)
        {
            App.Services.GetRequiredService<IShellService>().OpenUrl(AppLinks.Donate);
        }
    }

    // ---- Regole della modalità "Rinomina file" ----------------------------------------------------------------

    private void AddRule_Click(object? sender, RoutedEventArgs e)
    {
        var menu = new FAMenuFlyout();
        foreach (var kind in BatchRenameViewModel.RuleKinds)
        {
            menu.Items.Add(new FAMenuFlyoutItem { Text = kind.Label, Command = ViewModel.Batch.AddRuleCommand, CommandParameter = kind.Key });
        }
        menu.ShowAt(AddRuleButton);
    }

    // ---- Pannelli ---------------------------------------------------------------------------------------------

    private void Issue_Tapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is IssueItemViewModel issue)
        {
            ViewModel.Issues.RevealCommand.Execute(issue);
        }
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e) => await OpenSettingsAsync();

    private async Task OpenSettingsAsync()
    {
        var settings = App.Services.GetRequiredService<SettingsViewModel>();
        settings.IsMediaMode = ViewModel.IsMediaMode; // in "Rinomina file" niente chiavi API né formati dei film
        var previousLanguage = ViewModel.CurrentLanguage;
        var previousUiLanguage = Strings.Current.Language;
        var dialog = new SettingsDialog(settings);
        if (await dialog.ShowAsync(this) != FAContentDialogResult.Primary)
        {
            return;
        }
        // Il dialog avvia il salvataggio ma non lo aspetta: lo aspettiamo noi prima di ricalcolare i nomi.
        if (settings.SaveCommand.ExecutionTask is { } saving)
        {
            await saving;
        }
        if (Strings.Current.Language != previousUiLanguage)
        {
            // Nuova lingua dell'interfaccia: la finestra si ricrea con i testi nuovi (stato e cartella restano nel ViewModel).
            App.ReplaceMainWindow(this);
        }
        await ViewModel.SettingsSavedCommand.ExecuteAsync(previousLanguage);
    }

    // ---- Righe: scelta del risultato e "togli dall'elenco" --------------------------------------------------------

    private async void FileList_Tapped(object? sender, TappedEventArgs e)
    {
        // Un clic sui pulsanti della riga ("Scegli…", la X) non apre la scelta una seconda volta.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }
        if ((e.Source as Control)?.DataContext is FileItemViewModel item)
        {
            await ChooseMatchAsync(item);
        }
    }

    private async void ChooseMatch_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FileItemViewModel item)
        {
            await ChooseMatchAsync(item);
        }
    }

    private async void RemoveRow_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FileItemViewModel item)
        {
            await ViewModel.RemoveItemCommand.ExecuteAsync(item);
        }
    }

    /// <summary>Risultati già trovati e ricerca libera; "Usa questo" applica la scelta alla riga.</summary>
    private async Task ChooseMatchAsync(FileItemViewModel item)
    {
        if (!ViewModel.CanChooseMatch(item))
        {
            return;
        }
        var picker = ViewModel.CreateMatchPicker(item);
        var dialog = new MatchPickerDialog(picker);
        var result = await dialog.ShowAsync(this);
        picker.SearchCancelCommand.Execute(null);
        if (result == FAContentDialogResult.Primary && picker.SelectedChoice is { } choice)
        {
            ViewModel.ApplyMatch(item, choice.Candidate);
        }
    }

    // ---- Menu contestuale della lista: formato del nome e lingua al volo -------------------------------------

    private void List_ContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (ViewModel.IsBusy)
        {
            return;
        }
        // Tasto destro su una riga: formati del suo tipo; su un'area vuota: formati di tutti i tipi.
        var item = (args.Source as Control)?.DataContext as FileItemViewModel;
        args.Handled = true;
        if (ViewModel.IsBatchMode && item is null)
        {
            return; // in "Rinomina file" il menu ha senso solo su una riga
        }
        BuildContextMenu(item).ShowAt(FileList, showAtPointer: true);
    }

    private FAMenuFlyout BuildContextMenu(FileItemViewModel? item)
    {
        var menu = new FAMenuFlyout();
        if (ViewModel.IsBatchMode)
        {
            // Qui i nomi vengono dalle regole: niente formati né lingua, solo le azioni sul file.
            AddFileActions(menu, item);
            return menu;
        }

        MediaKind[] kinds = item?.TemplateKind is { } kind ? [kind] : Kinds;
        foreach (var k in kinds)
        {
            menu.Items.Add(FormatMenu(k, item));
        }

        var languages = new FAMenuFlyoutSubItem { Text = Strings.Current.TitleLanguage, IconSource = new FASymbolIconSource { Symbol = FASymbol.Globe } };
        foreach (var language in ViewModel.Languages)
        {
            languages.Items.Add(new FARadioMenuFlyoutItem
            {
                Text = language.Label,
                GroupName = "lingua",
                IsChecked = string.Equals(language.Tag, ViewModel.CurrentLanguage, StringComparison.OrdinalIgnoreCase),
                Command = ViewModel.SetLanguageCommand,
                CommandParameter = language.Tag,
            });
        }
        menu.Items.Add(languages);

        var custom = new FAMenuFlyoutItem { Text = Strings.Current.MenuCustomizeFormats, IconSource = new FASymbolIconSource { Symbol = FASymbol.Settings } };
        custom.Click += Settings_Click;
        menu.Items.Add(custom);
        if (item is not null)
        {
            menu.Items.Add(new FAMenuFlyoutSeparator());
        }
        AddFileActions(menu, item);
        return menu;
    }

    private void AddFileActions(FAMenuFlyout menu, FileItemViewModel? item)
    {
        if (item is not null)
        {
            if (ViewModel.CanChooseMatch(item))
            {
                var choose = new FAMenuFlyoutItem { Text = Strings.Current.ChooseMatch, IconSource = new FASymbolIconSource { Symbol = FASymbol.Find } };
                choose.Click += async (_, _) => await ChooseMatchAsync(item);
                menu.Items.Add(choose);
            }
            if (item.Entry.TargetPath is { } targetPath)
            {
                var copy = new FAMenuFlyoutItem { Text = Strings.Current.MenuCopyNewName, IconSource = new FASymbolIconSource { Symbol = FASymbol.Copy } };
                copy.Click += async (_, _) =>
                {
                    if (Clipboard is { } clipboard)
                    {
                        await clipboard.SetTextAsync(Path.GetFileName(targetPath));
                    }
                };
                menu.Items.Add(copy);
            }
            var reveal = new FAMenuFlyoutItem { Text = Strings.Current.MenuRevealFolder, IconSource = new FASymbolIconSource { Symbol = FASymbol.OpenFolder } };
            reveal.Click += (_, _) => App.Services.GetRequiredService<IShellService>().RevealInExplorer(item.Entry.SourcePath);
            menu.Items.Add(reveal);
            var remove = new FAMenuFlyoutItem { Text = Strings.Current.RemoveFromList, IconSource = new FASymbolIconSource { Symbol = FASymbol.Dismiss } };
            remove.Click += async (_, _) => await ViewModel.RemoveItemCommand.ExecuteAsync(item);
            menu.Items.Add(remove);
        }
    }

    private static string KindName(MediaKind kind) => kind switch
    {
        MediaKind.Movie => Strings.Current.KindMovie,
        MediaKind.Episode => Strings.Current.KindEpisode,
        MediaKind.Anime => Strings.Current.KindAnime,
        _ => Strings.Current.KindMusic,
    };

    private FAMenuFlyoutSubItem FormatMenu(MediaKind kind, FileItemViewModel? item)
    {
        var sub = new FAMenuFlyoutSubItem { Text = Strings.Current.Format(nameof(Strings.MenuNameFormat), KindName(kind)), IconSource = new FASymbolIconSource { Symbol = FASymbol.Rename } };
        var current = ViewModel.TemplateFor(kind);
        foreach (var preset in TemplatePresets.For(kind))
        {
            var option = new FARadioMenuFlyoutItem
            {
                Text = preset.Label,
                GroupName = $"formato-{kind}",
                IsChecked = preset.Pattern == current,
                Command = ViewModel.ApplyTemplatePresetCommand,
                CommandParameter = preset,
            };
            // Anteprima del risultato sulla riga cliccata.
            if (item is not null && ViewModel.PreviewName(item, preset) is { } preview)
            {
                ToolTip.SetTip(option, preview);
            }
            sub.Items.Add(option);
        }
        return sub;
    }
}
