using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
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
    private static readonly Dictionary<MediaKind, string> KindNames = new()
    {
        [MediaKind.Movie] = "film",
        [MediaKind.Episode] = "serie TV",
        [MediaKind.Anime] = "anime",
        [MediaKind.Music] = "musica",
    };

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
        DropZoneTitle.Text = active ? "Rilascia per analizzare" : "Trascina qui la cartella della tua libreria";
    }

    // ---- Date: su Linux la data di creazione si cambia solo su alcuni dischi ---------------------------------

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.RootFolder))
        {
            return;
        }
        var root = ViewModel.RootFolder;
        if (root is null || FileCreationTime.CanSet(root))
        {
            DateHintBar.IsOpen = false;
            return;
        }
        DateHintBar.Message =
            "Su questo disco Linux non permette di cambiare la data di creazione dei file: Renamr imposta la data di modifica " +
            "alla data di uscita e, se attivo, la scrive anche nei metadati interni. Sui dischi NTFS montati con ntfs-3g " +
            "(i dischi esterni di Windows) e sulle cartelle di rete SMB la data di creazione viene cambiata.";
        DateHintBar.IsOpen = true;
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
        var previousLanguage = ViewModel.CurrentLanguage;
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
        BuildContextMenu(item).ShowAt(FileList, showAtPointer: true);
    }

    private FAMenuFlyout BuildContextMenu(FileItemViewModel? item)
    {
        var menu = new FAMenuFlyout();

        MediaKind[] kinds = item?.TemplateKind is { } kind ? [kind] : [.. KindNames.Keys];
        foreach (var k in kinds)
        {
            menu.Items.Add(FormatMenu(k, item));
        }

        var languages = new FAMenuFlyoutSubItem { Text = "Lingua dei titoli", IconSource = new FASymbolIconSource { Symbol = FASymbol.Globe } };
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

        var custom = new FAMenuFlyoutItem { Text = "Personalizza formati…", IconSource = new FASymbolIconSource { Symbol = FASymbol.Settings } };
        custom.Click += Settings_Click;
        menu.Items.Add(custom);

        if (item is not null)
        {
            menu.Items.Add(new FAMenuFlyoutSeparator());
            if (item.Entry.TargetPath is { } targetPath)
            {
                var copy = new FAMenuFlyoutItem { Text = "Copia nuovo nome", IconSource = new FASymbolIconSource { Symbol = FASymbol.Copy } };
                copy.Click += async (_, _) =>
                {
                    if (Clipboard is { } clipboard)
                    {
                        await clipboard.SetTextAsync(Path.GetFileName(targetPath));
                    }
                };
                menu.Items.Add(copy);
            }
            var reveal = new FAMenuFlyoutItem { Text = "Mostra nella cartella", IconSource = new FASymbolIconSource { Symbol = FASymbol.OpenFolder } };
            reveal.Click += (_, _) => App.Services.GetRequiredService<IShellService>().RevealInExplorer(item.Entry.SourcePath);
            menu.Items.Add(reveal);
        }
        return menu;
    }

    private FAMenuFlyoutSubItem FormatMenu(MediaKind kind, FileItemViewModel? item)
    {
        var sub = new FAMenuFlyoutSubItem { Text = $"Formato nome ({KindNames[kind]})", IconSource = new FASymbolIconSource { Symbol = FASymbol.Rename } };
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
