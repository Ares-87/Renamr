using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Renamr.Core.Localization;
using Renamr.Core.Models;
using Renamr.Core.Templating;
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
        IssueList.Tapped += Issue_Tapped;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateDateHint();

        // Modalità: prima si mostra quella del ViewModel, poi si ascoltano i clic. Un TabStrip appena creato seleziona
        // da solo la prima voce, e con un binding questo rimetteva "Film, Serie e Musica" ricreando la finestra.
        Loaded += (_, _) =>
        {
            ModeTabs.SelectedIndex = ViewModel.ModeIndex;
            ModeTabs.SelectionChanged += ModeTabs_SelectionChanged;
        };
    }

    private void ModeTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ModeTabs.SelectedIndex >= 0)
        {
            ViewModel.ModeIndex = ModeTabs.SelectedIndex;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Chiudendo, il TabStrip può cambiare selezione: non deve arrivare al ViewModel (che resta alla finestra nuova).
        ModeTabs.SelectionChanged -= ModeTabs_SelectionChanged;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnClosed(e);
    }

    public MainViewModel ViewModel { get; }

    // ---- Drag & drop: si accetta una cartella, ovunque nella finestra ----------------------------------------

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
        // Cartella trascinata, oppure la cartella che contiene il primo file trascinato.
        var path = items.OfType<IStorageFolder>().FirstOrDefault()?.TryGetLocalPath()
                   ?? Path.GetDirectoryName(items.OfType<IStorageFile>().FirstOrDefault()?.TryGetLocalPath() ?? string.Empty);

        if (!string.IsNullOrEmpty(path))
        {
            await ViewModel.OpenFolderCommand.ExecuteAsync(path);
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
        if (e.PropertyName is nameof(MainViewModel.RootFolder) or nameof(MainViewModel.IsBatchMode))
        {
            UpdateDateHint();
        }
    }

    private void UpdateDateHint()
    {
        // In modalità "Rinomina file" le date non si toccano: l'avviso non serve.
        var root = ViewModel.RootFolder;
        if (root is null || ViewModel.IsBatchMode || FileCreationTime.CanSet(root))
        {
            DateHintBar.IsOpen = false;
            return;
        }
        DateHintBar.Message = Strings.Current.CreationDateHint;
        DateHintBar.IsOpen = true;
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
