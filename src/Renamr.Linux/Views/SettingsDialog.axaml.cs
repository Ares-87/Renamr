using FluentAvalonia.UI.Controls;
using Renamr.Presentation.ViewModels;

namespace Renamr.Linux.Views;

public sealed partial class SettingsDialog : FAContentDialog
{
    public SettingsDialog(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    // Senza questo FAContentDialog sottoclassato perderebbe il suo stile (lo stile si cerca per tipo).
    protected override Type StyleKeyOverride => typeof(FAContentDialog);
}
