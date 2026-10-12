using System.Windows;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

public partial class CodexActivityStrip
{
    private readonly CodexCrewMuteStore _muteStore = new();
    private CodexCrewMutePreferences _mutePreferences = CodexCrewMutePreferences.Empty;
    private readonly HashSet<string> _changingMute = new(StringComparer.Ordinal);
    private bool _changingMuteAll;
    private bool _muteLoaded;
    private string? _muteReadError;
    private int _muteRevision;

    public bool IsMuteAll => _mutePreferences.MuteAll;
    public string MuteAllButtonText => IsMuteAll ? "Unmute All" : "Mute All";
    public string MuteAllDescription => IsMuteAll
        ? "Restore automatic messages and waving/talking across all projects. Individual chat mute choices remain saved."
        : "Mute automatic messages and waving/talking across all projects, including new chats. Remember this choice securely on this PC.";
    public bool CanMuteAll => _lifetime is not null && _muteLoaded && !_clearing && !_resetting &&
        !_changingMuteAll && _changingMute.Count == 0 && _muteReadError is null;

    private (CodexCrewMutePreferences? Preferences, string? Error) ReadMute(CancellationToken token)
    {
        try { return (_muteStore.Read(token), null); }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex) { return (null, ex.Message); }
    }

    private void ApplyMuteRead((CodexCrewMutePreferences? Preferences, string? Error) read)
    {
        if (read.Preferences is not null) _mutePreferences = read.Preferences;
        _muteLoaded = true;
        _muteReadError = read.Error;
    }

    private async Task LoadMuteAsync(CancellationToken token)
    {
        var revision = _muteRevision;
        try
        {
            var read = await Task.Run(() => ReadMute(token), token);
            if (token.IsCancellationRequested || revision != _muteRevision) return;
            ApplyMuteRead(read);
            ApplyMuteCards();
            NotifyView();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ApplyMuteCards()
    {
        var canChange = _muteLoaded && !_changingMuteAll && _changingMute.Count == 0 && _muteReadError is null;
        var muteAll = IsMuteAll || !_muteLoaded || _muteReadError is not null;
        foreach (var card in Agents)
            card.SetMuted(muteAll || _mutePreferences.MutedChats.Contains(card.Id), canChange && !muteAll, muteAll);
    }

    private async void MuteAgent_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: ActivityCard card } || !card.CanChangeMute ||
            _lifetime is null || _changingMuteAll || _changingMute.Count != 0) return;
        await ChangeMuteAsync([card], !card.IsMuted, "Mute");
    }

    private async void MuteAll_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!CanMuteAll) return;
        var muted = !IsMuteAll;
        _changingMuteAll = true;
        try { await SaveMuteAsync(token => _muteStore.SetMuteAll(muted, token), MuteAllButtonText); }
        finally { _changingMuteAll = false; ApplyMuteCards(); NotifyView(); }
    }

    private async Task ChangeMuteAsync(IReadOnlyList<ActivityCard> cards, bool muted, string title)
    {
        if (_lifetime is null || _changingMuteAll || _changingMute.Count != 0 || cards.Count == 0) return;
        var ids = cards.Select(card => card.Id).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in ids) _changingMute.Add(id);
        try { await SaveMuteAsync(token => _muteStore.SetMuted(ids, muted, token), title); }
        finally
        {
            foreach (var id in ids) _changingMute.Remove(id);
            ApplyMuteCards();
            NotifyView();
        }
    }

    private async Task SaveMuteAsync(Func<CancellationToken, CodexCrewMutePreferences> save, string title)
    {
        if (_lifetime is null) return;
        var lifetime = _lifetime;
        var token = lifetime.Token;
        _muteRevision++;
        ApplyMuteCards();
        NotifyView();
        try
        {
            var saved = await Task.Run(() => save(token), token);
            if (token.IsCancellationRequested || !ReferenceEquals(_lifetime, lifetime)) return;
            _mutePreferences = saved;
            _muteLoaded = true;
            _muteReadError = null;
            // Toggling Mute closes messages already on screen, including child popups.
            if (saved.MuteAll || Agents.Any(card => saved.MutedChats.Contains(card.Id) &&
                    EnumerateActivityCards([card]).Any(value => ReferenceEquals(value, _hoverThoughtContext))))
                HideThoughtPopups();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (InvalidOperationException ex)
        {
            if (IsLoaded && ReferenceEquals(_lifetime, lifetime)) MessageBox.Show(Window.GetWindow(this), ex.Message, title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (ReferenceEquals(_lifetime, lifetime))
            {
                _muteRevision++;
                ApplyMuteCards();
                UpdateBubbleTimer();
                NotifyView();
            }
        }
    }
}
