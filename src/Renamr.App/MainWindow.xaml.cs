using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Renamr.App.Views;
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
    }

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
        e.DragUIOverride.Caption = "Apri questa cartella in Renamr";
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
        DropZoneTitle.Text = active ? "Rilascia per analizzare" : "Trascina qui la cartella della tua libreria";
    }

    // ---- Pannelli ---------------------------------------------------------------------------------------------

    private void Issue_ItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.Issues.RevealCommand.Execute(e.ClickedItem as IssueItemViewModel);

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(App.Services.GetRequiredService<SettingsViewModel>())
        {
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
