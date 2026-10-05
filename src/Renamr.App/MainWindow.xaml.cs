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
using Renamr.Presentation.Animation;
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
        // Icona nella barra delle applicazioni e in Alt+Tab (l'exe ha la stessa icona da ApplicationIcon).
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "renamr.ico"));

        // AppWindow lavora in pixel fisici: scaliamo la dimensione "logica" con i DPI del monitor.
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1120 * scale), (int)(740 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 520;
        }

        // Riparte dall'ultima modalità usata: le schede seguono il ViewModel, anche ricreando la finestra (cambio di lingua).
        ApplyModeTabs();
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        foreach (var tab in new[] { MediaTab, BatchTab })
        {
            tab.PointerEntered += (s, _) => { _hoveredTab = s; ApplyModeTabs(); };
            tab.PointerExited += (s, _) => { if (_hoveredTab == s) { _hoveredTab = null; } ApplyModeTabs(); };
        }

        // La tazzina del caffè: prima oscillazione poco dopo l'apertura, poi ogni tanto (se il sistema non ha spento le animazioni).
        _coffeeFrames = new DispatcherTimer { Interval = CoffeeWiggle.Frame };
        _coffeeFrames.Tick += (_, _) => CoffeeFrame();
        _coffeeTimer = new DispatcherTimer { Interval = CoffeeWiggle.FirstDelay };
        _coffeeTimer.Tick += (_, _) =>
        {
            _coffeeTimer.Interval = CoffeeWiggle.Interval;
            if (SystemUi.AnimationsEnabled)
            {
                _coffeeStart = DateTime.UtcNow;
                _coffeeFrames.Start();
            }
        };
        _coffeeTimer.Start();
        Closed += (_, _) =>
        {
            _coffeeTimer.Stop();
            _coffeeFrames.Stop();
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        };
    }

    private static readonly Windows.UI.ViewManagement.UISettings SystemUi = new();
    private readonly DispatcherTimer _coffeeTimer;
    private readonly DispatcherTimer _coffeeFrames;
    private DateTime _coffeeStart;
    private object? _hoveredTab;

    public MainViewModel ViewModel { get; }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    // ---- Drag & drop: una cartella oppure dei file, ovunque nella finestra ------------------------------------

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
        // Una cartella apre quella cartella; dei file si aggiungono all'elenco (o ne fanno uno nuovo dalla schermata iniziale).
        var folder = items.OfType<StorageFolder>().Select(f => f.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        var files = items.OfType<StorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
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
        var resources = Application.Current.Resources;
        DropZoneOutline.Stroke = (Microsoft.UI.Xaml.Media.Brush)resources[active ? "AccentFillColorDefaultBrush" : "ControlStrongStrokeColorDefaultBrush"];
        DropZoneOutline.Fill = (Microsoft.UI.Xaml.Media.Brush)resources[active ? "SubtleFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush"];
        DropZoneTitle.Text = active ? Strings.Current.DropRelease : ViewModel.DropZoneTitle;
    }

    // ---- Modalità e regole di "Rinomina file" -----------------------------------------------------------------

    private void MediaTab_Click(object sender, RoutedEventArgs e) => ViewModel.ModeIndex = 0;

    private void BatchTab_Click(object sender, RoutedEventArgs e) => ViewModel.ModeIndex = 1;

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsBatchMode) or nameof(MainViewModel.IsBusy))
        {
            ApplyModeTabs();
        }
    }

    // Stessi colori di Renamr.Linux: blu del marchio per film, serie e musica, arancione per "Rinomina file".
    private static readonly Windows.UI.Color MediaColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x2F, 0x6B, 0xFF);
    private static readonly Windows.UI.Color BatchColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xE8, 0x59, 0x0C);

    private void ApplyModeTabs()
    {
        ApplyModeTab(MediaTab, MediaTabIcon, [MediaTabGlyph1, MediaTabGlyph2], MediaColor, ViewModel.IsMediaMode);
        ApplyModeTab(BatchTab, BatchTabIcon, [BatchTabGlyph], BatchColor, ViewModel.IsBatchMode);
    }

    private void ApplyModeTab(Button tab, Border icon, FontIcon[] glyphs, Windows.UI.Color color, bool selected)
    {
        var hovered = ReferenceEquals(_hoveredTab, tab) && !ViewModel.IsBusy;
        tab.Background = Brush(color, selected ? (byte)0xFF : hovered ? (byte)0x30 : (byte)0x1A);
        tab.BorderBrush = Brush(color, selected ? (byte)0xFF : (byte)0x55);
        if (selected)
        {
            tab.Foreground = Brush(Microsoft.UI.Colors.White, 0xFF);
        }
        else
        {
            tab.ClearValue(Control.ForegroundProperty); // torna al colore del testo del tema (ModeTabStyle)
        }
        tab.Opacity = ViewModel.IsBusy ? 0.55 : 1;
        icon.Background = selected ? Brush(Microsoft.UI.Colors.White, 0x38) : Brush(color, 0x30);
        foreach (var glyph in glyphs)
        {
            glyph.Foreground = selected ? Brush(Microsoft.UI.Colors.White, 0xFF) : Brush(color, 0xFF);
        }
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush Brush(Windows.UI.Color color, byte alpha) =>
        new(Microsoft.UI.ColorHelper.FromArgb(alpha, color.R, color.G, color.B));

    // ---- Offri un caffè ---------------------------------------------------------------------------------------

    private void CoffeeFrame()
    {
        var elapsed = DateTime.UtcNow - _coffeeStart;
        var (angle, rise, opacity) = CoffeeWiggle.At(elapsed);
        CoffeeTilt.Angle = angle;
        CoffeeSteamRise.Y = -rise;
        CoffeeSteam.Opacity = opacity;
        if (elapsed >= CoffeeWiggle.Duration)
        {
            _coffeeFrames.Stop();
        }
    }

    private async void Coffee_Click(object sender, RoutedEventArgs e)
    {
        var resources = Application.Current.Resources;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
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
                        Style = (Style)resources["CaptionTextBlockStyle"],
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)resources["TextFillColorSecondaryBrush"],
                    },
                },
            },
            PrimaryButtonText = Strings.Current.DonateButton,
            CloseButtonText = Strings.Current.DonateLater,
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            App.Services.GetRequiredService<IShellService>().OpenUrl(AppLinks.Donate);
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

    // ---- Righe: scelta del risultato e "togli dall'elenco" --------------------------------------------------------

    private async void FileList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileItemViewModel item)
        {
            await ChooseMatchAsync(item);
        }
    }

    private async void ChooseMatch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileItemViewModel item)
        {
            await ChooseMatchAsync(item);
        }
    }

    private async void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileItemViewModel item)
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
        var dialog = new MatchPickerDialog(picker) { XamlRoot = Content.XamlRoot };
        var result = await dialog.ShowAsync();
        picker.SearchCancelCommand.Execute(null);
        if ((result == ContentDialogResult.Primary || dialog.Confirmed) && picker.SelectedChoice is { } choice)
        {
            ViewModel.ApplyMatch(item, choice.Candidate);
        }
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

    private void AddFileActions(MenuFlyout menu, FileItemViewModel? item)
    {
        if (item is not null)
        {
            if (ViewModel.CanChooseMatch(item))
            {
                var choose = new MenuFlyoutItem { Text = Strings.Current.ChooseMatch, Icon = new FontIcon { Glyph = "\uE721" } };
                choose.Click += async (_, _) => await ChooseMatchAsync(item);
                menu.Items.Add(choose);
            }
            if (item.Entry.TargetPath is not null)
            {
                var copy = new MenuFlyoutItem { Text = Strings.Current.MenuCopyNewName, Icon = new FontIcon { Glyph = "\uE8C8" } };
                copy.Click += (_, _) => CopyToClipboard(Path.GetFileName(item.Entry.TargetPath));
                menu.Items.Add(copy);
            }
            var reveal = new MenuFlyoutItem { Text = Strings.Current.MenuRevealExplorer, Icon = new FontIcon { Glyph = "\uEC50" } };
            reveal.Click += (_, _) => App.Services.GetRequiredService<IShellService>().RevealInExplorer(item.Entry.SourcePath);
            menu.Items.Add(reveal);
            var remove = new MenuFlyoutItem { Text = Strings.Current.RemoveFromList, Icon = new FontIcon { Glyph = "\uE711" } };
            remove.Click += async (_, _) => await ViewModel.RemoveItemCommand.ExecuteAsync(item);
            menu.Items.Add(remove);
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
