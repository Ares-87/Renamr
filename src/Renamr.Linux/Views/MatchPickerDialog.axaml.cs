using Avalonia.Input;
using FluentAvalonia.UI.Controls;
using Renamr.Presentation.ViewModels;

namespace Renamr.Linux.Views;

public sealed partial class MatchPickerDialog : FAContentDialog
{
    public MatchPickerDialog(MatchPickerViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // Invio nel titolo = cerca (non "Usa questo"); doppio clic su un risultato = usalo subito.
        QueryBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (viewModel.SearchCommand.CanExecute(null))
                {
                    viewModel.SearchCommand.Execute(null);
                }
            }
        };
        QueryBox.KeyUp += (_, e) => e.Handled |= e.Key == Key.Enter; // Invio serve a cercare, non a chiudere
        ChoiceList.DoubleTapped += (_, _) =>
        {
            if (viewModel.CanApply)
            {
                Hide(FAContentDialogResult.Primary);
            }
        };
    }

    // Senza questo FAContentDialog sottoclassato perderebbe il suo stile (lo stile si cerca per tipo).
    protected override Type StyleKeyOverride => typeof(FAContentDialog);
}
