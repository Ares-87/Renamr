using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Renamr.Presentation.ViewModels;
using Windows.System;

namespace Renamr.App.Views;

public sealed partial class MatchPickerDialog : ContentDialog
{
    public MatchPickerDialog(MatchPickerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public MatchPickerViewModel ViewModel { get; }

    /// <summary>Invio nel titolo = cerca (non "Usa questo").</summary>
    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (ViewModel.SearchCommand.CanExecute(null))
            {
                ViewModel.SearchCommand.Execute(null);
            }
        }
    }

    /// <summary>Il dialog userebbe Invio per il pulsante principale: qui serve a cercare.</summary>
    private void QueryBox_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
        }
    }

    /// <summary>Doppio clic su un risultato = usalo subito.</summary>
    private void ChoiceList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.CanApply)
        {
            // Hide() chiude con None: si segna la scelta come se si fosse premuto "Usa questo".
            Confirmed = true;
            Hide();
        }
    }

    /// <summary>Chiuso con il doppio clic su un risultato.</summary>
    public bool Confirmed { get; private set; }
}
