using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Renamr.Presentation.Messages;
using Renamr.Presentation.Services;

namespace Renamr.Presentation.ViewModels;

/// <summary>
/// Pannello collassabile Errori/Avvisi. È disaccoppiato dalla finestra principale tramite Messenger:
/// chiunque (pipeline, ViewModel, servizi futuri) può segnalare un problema senza conoscere questo pannello.
/// </summary>
public sealed partial class IssuesViewModel : ObservableRecipient,
    IRecipient<FileIssueMessage>,
    IRecipient<RunStartedMessage>
{
    private readonly IShellService? _shell;

    public IssuesViewModel(IMessenger messenger, IShellService? shell = null) : base(messenger)
    {
        _shell = shell;
        IsActive = true; // registra i recipient
    }

    public ObservableCollection<IssueItemViewModel> Issues { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header), nameof(HasIssues))]
    public partial int ErrorCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header), nameof(HasIssues))]
    public partial int WarningCount { get; private set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public bool HasIssues => ErrorCount + WarningCount > 0;

    public string Header => (ErrorCount, WarningCount) switch
    {
        (0, 0) => "Nessun problema",
        (var e, 0) => $"{e} errori",
        (0, var w) => $"{w} avvisi",
        var (e, w) => $"{e} errori · {w} avvisi",
    };

    public void Receive(FileIssueMessage message)
    {
        Issues.Add(new IssueItemViewModel(message.FilePath, message.Error, message.Phase));
        if (message.Error.IsWarning)
        {
            WarningCount++;
        }
        else
        {
            ErrorCount++;
        }
    }

    public void Receive(RunStartedMessage message)
    {
        Issues.Clear();
        ErrorCount = WarningCount = 0;
        IsExpanded = false;
    }

    [RelayCommand]
    private void Reveal(IssueItemViewModel? issue)
    {
        if (issue is not null)
        {
            _shell?.RevealInExplorer(issue.FilePath);
        }
    }
}
