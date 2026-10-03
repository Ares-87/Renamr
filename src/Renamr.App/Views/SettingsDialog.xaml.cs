using Microsoft.UI.Xaml.Controls;
using Renamr.Presentation.ViewModels;

namespace Renamr.App.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
