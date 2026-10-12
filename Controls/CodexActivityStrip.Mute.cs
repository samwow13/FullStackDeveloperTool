using System.Windows;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

public partial class CodexActivityStrip
{
    private readonly CodexCrewMuteStore _muteStore = new();
    private IReadOnlySet<string> _mutedChats = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _changingMute = new(StringComparer.Ordinal);
    private string? _muteReadError;
    private int _muteRevision;

    public bool CanMuteAll => !string.IsNullOrWhiteSpace(_selectedProjectId) && _lifetime is not null &&
        !_readFailed && _snapshot?.IsAvailable == true && !_clearing && !_resetting &&
        _changingMute.Count == 0 && _muteReadError is null && Agents.Any(card => !card.IsMuted);

    private (IReadOnlySet<string>? Chats, string? Error) ReadMute(CancellationToken token)
    {
        try { return (_muteStore.Read(token), null); }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex) { return (null, ex.Message); }
    }

    private void ApplyMuteRead((IReadOnlySet<string>? Chats, string? Error) read)
    {
        if (read.Chats is not null) _mutedChats = read.Chats;
        _muteReadError = read.Error;
    }

    private void ApplyMuteCards()
    {
        foreach (var card in Agents)
            card.SetMuted(_mutedChats.Contains(card.Id), _changingMute.Count == 0 && _muteReadError is null);
    }

    private async void MuteAgent_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: ActivityCard card } || !card.CanChangeMute ||
            _lifetime is null || _changingMute.Count != 0) return;
        await ChangeMuteAsync([card], !card.IsMuted, "Mute");
    }

    private async void MuteAll_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!CanMuteAll) return;
        var projectId = _selectedProjectId;
        var scopedIds = _snapshot!.Agents.Where(agent => agent.ProjectId == projectId)
            .Select(agent => agent.Id).ToHashSet(StringComparer.Ordinal);
        // Freeze the selected project's roots before the save can yield. A project
        // switch or newly arriving chat must never retarget this explicit action.
        var cards = Agents.Where(card => scopedIds.Contains(card.Id) && !card.IsMuted).ToArray();
        if (cards.Length != 0) await ChangeMuteAsync(cards, true, "Mute All");
    }

    private async Task ChangeMuteAsync(IReadOnlyList<ActivityCard> cards, bool muted, string title)
    {
        if (_lifetime is null || _changingMute.Count != 0 || cards.Count == 0) return;
        var token = _lifetime.Token;
        var ids = cards.Select(card => card.Id).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in ids) _changingMute.Add(id);
        _muteRevision++;
        ApplyMuteCards();
        NotifyView();
        try
        {
            var saved = await Task.Run(() => _muteStore.SetMuted(ids, muted, token), token);
            _mutedChats = saved;
            _muteReadError = null;
            // Toggling Mute closes messages already on screen, including child popups.
            if (muted && EnumerateActivityCards(cards).Any(value => ReferenceEquals(value, _hoverThoughtContext)))
                ClearThoughtHover();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (InvalidOperationException ex)
        {
            if (IsLoaded) MessageBox.Show(Window.GetWindow(this), ex.Message, title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            foreach (var id in ids) _changingMute.Remove(id);
            _muteRevision++;
            ApplyMuteCards();
            UpdateBubbleTimer();
            NotifyView();
        }
    }
}
