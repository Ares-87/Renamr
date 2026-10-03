using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Extensions.DependencyInjection;
using Renamr.App.Views;
using Renamr.Core.Localization;
using Renamr.Core.Models;
using Renamr.Core.Templating;
using Renamr.Presentation.Services;
using Renamr.Presentation.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;

namespace Renamr.App;

/// <summary>
/// Code-behind ridotto a ciò che è puramente "vista": barra del titolo, feedback visivo del drag &amp; drop,
/// apertura del dialog. Ogni azione vera passa dai comandi del ViewModel.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        Title = viewModel.AppTitle;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        // AppWindow lavora in pixel fisici: scaliamo la dimensione "logica" con i DPI del monitor.
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1120 * scale), (int)(740 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 520;
        }

        // Riparte dall'ultima modalità usata. I clic si ascoltano solo a finestra caricata: un selettore appena creato può
        // scegliere da solo la prima voce, e ricreando la finestra (cambio di lingua) rimetterebbe "Film, Serie e Musica".
        ModeBar.SelectedItem = viewModel.IsBatchMode ? BatchModeItem : MediaModeItem;
        RootGrid.Loaded += (_, _) =>
        {
            ModeBar.SelectedItem = ViewModel.IsBatchMode ? BatchModeItem : MediaModeItem;
            _modeBarReady = true;
        };
    }

    private bool _modeBarReady;

    public MainViewModel ViewModel { get; }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    // ---- Drag & drop: si accetta una cartella, ovunque nella finestra ----------------------------------------

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.IsBusy || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = Strings.Current.DropCaption;
        e.DragUIOverride.IsGlyphVisible = false;
        SetDropHighlight(true);
    }

    private void Root_DragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        // Cartella trascinata, oppure la cartella che contiene il primo file trascinato.
        var path = items.OfType<StorageFolder>().FirstOrDefault()?.Path
                   ?? Path.GetDirectoryName(items.OfType<StorageFile>().FirstOrDefault()?.Path ?? string.Empty);

        if (!string.IsNullOrEmpty(path))
        {
            await ViewModel.OpenFolderCommand.ExecuteAsync(path);
        }
    }

    private void SetDropHighlight(bool active)
    {
        var resources = Application.Current.Resources;
        DropZoneOutline.Stroke = (Microsoft.UI.Xaml.Media.Brush)resources[active ? "AccentFillColorDefaultBrush" : "ControlStrongStrokeColorDefaultBrush"];
        DropZoneOutline.Fill = (Microsoft.UI.Xaml.Media.Brush)resources[active ? "SubtleFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush"];
        DropZoneTitle.Text = active ? Strings.Current.DropRelease : ViewModel.DropZoneTitle;
    }

    // ---- Modalità e regole di "Rinomina file" -----------------------------------------------------------------

    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_modeBarReady && sender.SelectedItem is not null)
        {
            ViewModel.ModeIndex = sender.SelectedItem == BatchModeItem ? 1 : 0;
        }
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
        foreach (var kind in BatchRenameViewModel.RuleKinds)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = kind.Label, Command = ViewModel.Batch.AddRuleCommand, CommandParameter = kind.Key });
        }
        menu.ShowAt(AddRuleButton);
    }

    // ---- Pannelli ---------------------------------------------------------------------------------------------

    private void Issue_ItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.Issues.RevealCommand.Execute(e.ClickedItem as IssueItemViewModel);

    private async void Settings_Click(object sender, RoutedEventArgs e) => await OpenSettingsAsync();

    private async Task OpenSettingsAsync()
    {
        var settings = App.Services.GetRequiredService<SettingsViewModel>();
        settings.IsMediaMode = ViewModel.IsMediaMode; // in "Rinomina file" niente chiavi API né formati dei film
        var previousLanguage = ViewModel.CurrentLanguage;
        var previousUiLanguage = Strings.Current.Language;
        var dialog = new SettingsDialog(settings)
        {
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
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
            ((App)Application.Current).ReplaceMainWindow(this);
        }
        await ViewModel.SettingsSavedCommand.ExecuteAsync(previousLanguage);
    }

    // ---- Menu contestuale della lista: formato del nome e lingua al volo -------------------------------------

    private static readonly MediaKind[] Kinds = [MediaKind.Movie, MediaKind.Episode, MediaKind.Anime, MediaKind.Music];

    private static string KindName(MediaKind kind) => kind switch
    {
        MediaKind.Movie => Strings.Current.KindMovie,
        MediaKind.Episode => Strings.Current.KindEpisode,
        MediaKind.Anime => Strings.Current.KindAnime,
        _ => Strings.Current.KindMusic,
    };

    /// <summary>Prima di chiudere la finestra vecchia (cambio di lingua): smette di seguire il ViewModel condiviso.</summary>
    public void StopTracking() => Bindings.StopTracking();

    private void Row_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (ViewModel.IsBusy)
        {
            return;
        }
        var item = (sender as FrameworkElement)?.DataContext as FileItemViewModel;
        ShowContextMenu(sender, args, item);
    }

    /// <summary>Tasto destro su un'area vuota della lista: formati di tutti i tipi.</summary>
    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (!args.Handled && !ViewModel.IsBusy)
        {
            ShowContextMenu(sender, args, null);
        }
    }

    private void ShowContextMenu(UIElement target, ContextRequestedEventArgs args, FileItemViewModel? item)
    {
        args.Handled = true;
        var menu = new MenuFlyout();
        if (ViewModel.IsBatchMode)
        {
            // Qui i nomi vengono dalle regole: niente formati né lingua, solo le azioni sul file.
            if (item is not null)
            {
                AddFileActions(menu, item);
                ShowMenu(menu, target, args);
            }
            return;
        }

        MediaKind[] kinds = item?.TemplateKind is { } kind ? [kind] : Kinds;
        foreach (var k in kinds)
        {
            menu.Items.Add(FormatMenu(k, item));
        }

        var languages = new MenuFlyoutSubItem { Text = Strings.Current.TitleLanguage, Icon = new FontIcon { Glyph = "\uE8F2" } };
        foreach (var language in ViewModel.Languages)
        {
            languages.Items.Add(new RadioMenuFlyoutItem
            {
                Text = language.Label,
                GroupName = "lingua",
                IsChecked = string.Equals(language.Tag, ViewModel.CurrentLanguage, StringComparison.OrdinalIgnoreCase),
                Command = ViewModel.SetLanguageCommand,
                CommandParameter = language.Tag,
            });
        }
        menu.Items.Add(languages);

        var custom = new MenuFlyoutItem { Text = Strings.Current.MenuCustomizeFormats, Icon = new FontIcon { Glyph = "\uE713" } };
        custom.Click += Settings_Click;
        menu.Items.Add(custom);
        if (item is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        AddFileActions(menu, item);
        ShowMenu(menu, target, args);
    }

    private static void AddFileActions(MenuFlyout menu, FileItemViewModel? item)
    {
        if (item is not null)
        {
            if (item.Entry.TargetPath is not null)
            {
                var copy = new MenuFlyoutItem { Text = Strings.Current.MenuCopyNewName, Icon = new FontIcon { Glyph = "\uE8C8" } };
                copy.Click += (_, _) => CopyToClipboard(Path.GetFileName(item.Entry.TargetPath));
                menu.Items.Add(copy);
            }
            var reveal = new MenuFlyoutItem { Text = Strings.Current.MenuRevealExplorer, Icon = new FontIcon { Glyph = "\uEC50" } };
            reveal.Click += (_, _) => App.Services.GetRequiredService<IShellService>().RevealInExplorer(item.Entry.SourcePath);
            menu.Items.Add(reveal);
        }
    }

    private static void ShowMenu(MenuFlyout menu, UIElement target, ContextRequestedEventArgs args)
    {
        if (args.TryGetPosition(target, out var point))
        {
            menu.ShowAt(target, point);
        }
        else
        {
            menu.ShowAt(target as FrameworkElement);
        }
    }

    private MenuFlyoutSubItem FormatMenu(MediaKind kind, FileItemViewModel? item)
    {
        var sub = new MenuFlyoutSubItem { Text = Strings.Current.Format(nameof(Strings.MenuNameFormat), KindName(kind)), Icon = new FontIcon { Glyph = "\uE8AC" } };
        var current = ViewModel.TemplateFor(kind);
        foreach (var preset in TemplatePresets.For(kind))
        {
            var option = new RadioMenuFlyoutItem
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
                ToolTipService.SetToolTip(option, preview);
            }
            sub.Items.Add(option);
        }
        return sub;
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
