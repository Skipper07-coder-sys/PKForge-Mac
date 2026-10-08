using AVFoundation;
using Foundation;
using PKForge.Domain;

namespace PKForge.App.Platforms.MacCatalyst;

/// <summary>
/// AVAudioPlayer background music, mirroring the Android player: persisted library, order and
/// autostart in Preferences; tracks are file paths played in place, never copied.
/// </summary>
public sealed class MusicPlayer : IMusicPlayer, IDisposable
{
    private AVAudioPlayer? _player;
    private readonly List<MusicTrack> _library = [];
    private readonly Random _rng = new();
    private int? _index;

    public IReadOnlyList<MusicTrack> Library => _library;
    public bool IsPlaying => _player?.Playing == true;
    public int? CurrentIndex => _index;
    public MusicOrder Order
    {
        get => Enum.TryParse<MusicOrder>(Preferences.Default.Get("music_order", nameof(MusicOrder.InOrder)), out var order) ? order : MusicOrder.InOrder;
        set => Preferences.Default.Set("music_order", value.ToString());
    }
    public bool Autostart
    {
        get => Preferences.Default.Get("music_autostart", false);
        set => Preferences.Default.Set("music_autostart", value);
    }

    /// <summary>Set when playback fails; the UI shows it so failures are never silent.</summary>
    public string? LastError { get; private set; }

    public MusicPlayer()
    {
        var saved = Preferences.Default.Get("music_library", "");
        foreach (var entry in saved.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('\u0001');
            if (parts.Length == 2) _library.Add(new MusicTrack(parts[0], parts[1]));
        }
    }

    public int Add(IReadOnlyList<PickedDocument> documents)
    {
        var added = 0;
        foreach (var document in documents)
        {
            if (_library.Any(t => t.DocumentId == document.DocumentId)) continue;
            _library.Add(new MusicTrack(document.DocumentId, document.DisplayName));
            added++;
        }
        Persist();
        return added;
    }

    public void Remove(int index)
    {
        if ((uint)index >= _library.Count) return;
        if (_index == index) { StopInternal(); _index = null; }
        else if (_index > index) _index--;
        _library.RemoveAt(index);
        Persist();
    }

    public void Clear()
    {
        StopInternal();
        _library.Clear();
        _index = null;
        Persist();
    }

    public void Play()
    {
        if (_library.Count == 0 || IsPlaying) return;
        // Resume a paused track where it stopped instead of restarting it.
        if (_player is { Playing: false } paused) { paused.Play(); return; }
        _index ??= 0;
        PlayIndex(_index.Value);
    }

    public void Pause() => _player?.Pause();

    public void Skip()
    {
        if (_library.Count == 0) return;
        PlayIndex(NextIndex());
    }

    private int NextIndex() => Order == MusicOrder.Shuffle
        ? _rng.Next(_library.Count)
        : ((_index ?? 0) + 1) % _library.Count;

    public void SetOrder(MusicOrder order) => Order = order;

    public void SetAutostart(bool on) => Autostart = on;

    /// <summary>Starts playback at app launch when the user asked for it.</summary>
    public void MaybeAutostart()
    {
        if (Autostart && _library.Count > 0) Play();
    }

    private bool _pausedForOverlay;

    /// <summary>Something with its own sound took over (the About easter egg): music steps aside.</summary>
    public void PauseForBackground()
    {
        if (_player?.Playing != true) return;
        _player.Pause();
        _pausedForOverlay = true;
    }

    /// <summary>Music paused by <see cref="PauseForBackground"/> carries on.</summary>
    public void ResumeFromBackground()
    {
        if (!_pausedForOverlay) return;
        _pausedForOverlay = false;
        _player?.Play();
    }

    private void PlayIndex(int index)
    {
        StopInternal();
        // An unplayable track skips forward, but never more than one lap of the library.
        for (var attempt = 0; attempt < _library.Count; attempt++)
        {
            _index = index;
            var track = _library[index];
            LastError = null;
            var player = AVAudioPlayer.FromUrl(NSUrl.FromFilename(track.DocumentId), out var error);
            if (player is not null && player.PrepareToPlay() && player.Play())
            {
                player.FinishedPlaying += OnFinished;
                _player = player;
                return;
            }
            player?.Dispose();
            LastError = error?.LocalizedDescription ?? $"{track.Title} could not be played.";
            index = NextIndex();
        }
    }

    private void OnFinished(object? sender, AVStatusEventArgs e) => MainThread.BeginInvokeOnMainThread(Skip);

    private void StopInternal()
    {
        if (_player is null) return;
        _player.FinishedPlaying -= OnFinished;
        _player.Stop();
        _player.Dispose();
        _player = null;
    }

    private void Persist() =>
        Preferences.Default.Set("music_library", string.Join("\n", _library.Select(t => $"{t.DocumentId}\u0001{t.Title}")));

    public void Dispose() => StopInternal();
}
