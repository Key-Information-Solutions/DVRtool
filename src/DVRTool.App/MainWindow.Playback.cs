using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DVRTool.Core;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DVRTool.App;

/// <summary>
/// The Playback / Export tab: a day of one camera on a timeline, footage drawn where it exists,
/// click to watch from there, drag to choose a range to export.
/// </summary>
/// <remarks>
/// <para>
/// <b>The video comes over the web port, never RTSP.</b> Every recorder streams a time range
/// for export over the port it is administered on (<see cref="IPlaybackClient"/>), and that
/// body fed to LibVLC through a <see cref="StreamMediaInput"/> plays at the footage's own pace —
/// exactly the way the Live tab's SDK route feeds it a program stream. RTSP playback would need
/// the RTSP port forwarded, and across the fleet it is not; the web port is, everywhere,
/// including the DW Cloud relay. So a seek is a new request from a new time, not a seek inside
/// the stream, and pausing pauses the download: the decoder stops reading, the socket fills,
/// the recorder waits.
/// </para>
/// <para>
/// <b>Dahua goes through ffmpeg.</b> Its footage is DHAV, and the LibVLC build we ship has no
/// demuxer for it (no avformat plugin at all), so <see cref="ContainerPipe"/> rewraps it as
/// MPEG-TS on the fly — a stream copy, the same ffmpeg the export's remux needs.
/// </para>
/// <para>
/// <b>Where the playhead is</b> is the requested start plus the decoder's media clock
/// (<see cref="PlaybackClock"/>). The recorder clips a request to what exists, so a click in a
/// gap is first snapped to the next footage (<see cref="FootageCoverage.NextFootageAt"/>) and
/// only then requested — otherwise the clock would run from a time the picture never showed.
/// When a run of footage ends, the next one is requested automatically; when the day's footage
/// ends, playback stops and says so.
/// </para>
/// <para>
/// The segment list the tab started life as is still here, behind a toggle: the timeline is the
/// primary view, but a row per file is what an evidence request sometimes wants, and
/// double-clicking one plays it.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private readonly PlaybackClock _playbackClock = new();
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>Bumped on every seek and stop; a start still in flight compares against it.</summary>
    private int _playbackGen;

    private PlaybackStream? _playbackStream;
    private ContainerPipe? _playbackPipe;
    private Task? _playbackStartTask;

    /// <summary>Guards a day load against a later day, channel or device.</summary>
    private int _playbackLoadGen;

    private IReadOnlyList<FootageSpan> _playbackCoverage = [];
    private bool _playbackDayLoaded;
    private bool _playbackDirty = true;
    private bool _playbackPaused;
    private bool _playbackEnded;
    private (int Year, int Month)? _playbackCalendarMonth;

    private static readonly float[] PlaybackRates = [1f, 2f, 4f, 8f];

    private void InitializePlaybackTab()
    {
        PlaybackDatePicker.SelectedDate = DateTime.Today;
        PlaybackTimeline.SeekRequested += (_, e) => _ = SeekPlaybackAsync(e.Time);
        PlaybackTimeline.SelectionChanged += (_, _) => UpdatePlaybackSelectionLabel();
        _playbackTimer.Tick += (_, _) => UpdatePlayback();
        _playbackTimer.Start();

        if (_playbackPlayer is { } player)
        {
            // LibVLC raises these on its own thread; the handlers only set flags the timer
            // reads on the UI thread, so nothing here touches the player or the window.
            player.EndReached += (_, _) => _playbackEnded = true;
            player.EncounteredError += (_, _) => _playbackEnded = true;
        }
        UpdatePlaybackButtons();
    }

    private IPlaybackClient? PlaybackClient => _client as IPlaybackClient;

    private StreamType SelectedPlaybackStream =>
        PlaybackStreamCombo.SelectedIndex == 1 ? StreamType.Sub : StreamType.Main;

    private DateTime PlaybackDay => (PlaybackDatePicker.SelectedDate ?? DateTime.Today).Date;

    // ----- the day -----

    private void OnPlaybackDateChanged(object? sender, SelectionChangedEventArgs e) =>
        _ = LoadPlaybackDayAsync();

    private void OnPlaybackPrevDay(object sender, RoutedEventArgs e) =>
        PlaybackDatePicker.SelectedDate = PlaybackDay.AddDays(-1);

    private void OnPlaybackNextDay(object sender, RoutedEventArgs e) =>
        PlaybackDatePicker.SelectedDate = PlaybackDay.AddDays(1);

    private void OnPlaybackStreamChanged(object sender, SelectionChangedEventArgs e)
    {
        // Re-request the same moment on the other stream, if something is playing.
        if (_playbackClock.Position is DateTime at && _playbackStream is not null)
            _ = SeekPlaybackAsync(at);
    }

    /// <summary>The channel list moved: the tab shows a different camera now.</summary>
    private void OnChannelSelected(object sender, SelectionChangedEventArgs e)
    {
        StopPlayback(clearClock: true);
        _playbackDirty = true;
        if (IsPlaybackTabVisible)
            _ = LoadPlaybackDayAsync();
    }

    private void OnMainTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles up from every combo and grid inside the tabs.
        if (!ReferenceEquals(e.OriginalSource, MainTabs))
            return;
        if (IsPlaybackTabVisible && _playbackDirty)
            _ = LoadPlaybackDayAsync();
    }

    private bool IsPlaybackTabVisible => ReferenceEquals(MainTabs.SelectedItem, PlaybackTab);

    /// <summary>Device gone or changed: nothing on this tab refers to it any more.</summary>
    private void ResetPlaybackTab()
    {
        StopPlayback(clearClock: true);
        _playbackLoadGen++;
        _playbackCoverage = [];
        _playbackDayLoaded = false;
        _playbackDirty = true;
        _playbackCalendarMonth = null;
        PlaybackTimeline.Coverage = [];
        PlaybackTimeline.Selection = null;
        PlaybackDaysText.Text = "";
        ResultsGrid.ItemsSource = null;
    }

    /// <summary>
    /// Reads the selected day's segments for the selected camera onto the timeline, and the
    /// month's recorded days for the date row when the month changed.
    /// </summary>
    private async Task LoadPlaybackDayAsync()
    {
        int gen = ++_playbackLoadGen;
        int selection = _selectionGen;
        _playbackDirty = false;
        _playbackDayLoaded = false;
        _playbackCoverage = [];
        var day = PlaybackDay;
        PlaybackTimeline.Window = TimelineWindow.Day(day);
        PlaybackTimeline.Coverage = [];
        PlaybackTimeline.Selection = null;
        ResultsGrid.ItemsSource = null;

        if (_client is null || _clientCts is null || ChannelList.SelectedItem is not ChannelItem item)
        {
            if (IsPlaybackTabVisible)
                SetStatus("Select a device and channel to see its recordings.");
            return;
        }
        if (PlaybackClient is not { } playback)
        {
            SetStatus($"Playback is not implemented for {_client.Vendor} devices.");
            return;
        }

        var client = _client;
        var ct = _clientCts.Token;
        int channel = item.Channel.Id;
        var searchTask = client.SearchAsync(channel, day, day.AddDays(1), ct);
        _searchTask = searchTask; // a device switch defers client disposal until this ends
        try
        {
            SetStatus($"Reading {day:yyyy-MM-dd} on channel {channel} …");
            var segments = await searchTask;
            if (gen != _playbackLoadGen || selection != _selectionGen)
                return;

            _playbackCoverage = FootageCoverage.Merge(segments);
            _playbackDayLoaded = true;
            PlaybackTimeline.Coverage = _playbackCoverage;
            ResultsGrid.ItemsSource = segments;

            // Clipped to the day: a recorder's segment can start before midnight, and the
            // status line is about this day, not about the file that straddles into it.
            var total = FootageCoverage.Total(FootageCoverage.Merge(segments.Select(s => s with
            {
                Start = s.Start < day ? day : s.Start,
                End = s.End > day.AddDays(1) ? day.AddDays(1) : s.End,
            })));
            SetStatus(segments.Count == 0
                ? $"No recordings on {day:yyyy-MM-dd} for channel {channel}."
                : $"{day:yyyy-MM-dd}, channel {channel}: {segments.Count} segment(s), " +
                  $"{(long)total.TotalHours}:{total.Minutes:D2}:{total.Seconds:D2} of footage. " +
                  "Click the timeline to play from there; drag to select a range to export.");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (gen != _playbackLoadGen || selection != _selectionGen)
                return;
            SetStatus($"Recording search failed: {Shorten(ex.Message)}");
            return;
        }
        finally
        {
            if (ReferenceEquals(searchTask, _searchTask))
                _searchTask = null;
        }

        if (_playbackCalendarMonth != (day.Year, day.Month))
            await LoadPlaybackCalendarAsync(playback, channel, day, gen, selection, ct);
    }

    private async Task LoadPlaybackCalendarAsync(IPlaybackClient playback, int channel,
        DateTime day, int gen, int selection, CancellationToken ct)
    {
        PlaybackDaysText.Text = "…";
        try
        {
            var days = await playback.GetRecordedDaysAsync(channel, day.Year, day.Month, ct);
            if (gen != _playbackLoadGen || selection != _selectionGen)
                return;
            _playbackCalendarMonth = (day.Year, day.Month);
            PlaybackDaysText.Text = days.Count == 0
                ? $"{day:MMMM}: nothing recorded"
                : $"{day:MMMM}: {DescribeDays(days)}";
            PlaybackDaysText.ToolTip = "Days of the month holding footage for this camera.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct) || ex is NotSupportedException)
        {
            if (gen != _playbackLoadGen || selection != _selectionGen)
                return;
            PlaybackDaysText.Text = "";
            PlaybackDaysText.ToolTip = $"The recorded-days calendar could not be read: {Shorten(ex.Message)}";
        }
    }

    /// <summary>"1–5, 8, 12–14": runs of consecutive days collapsed.</summary>
    internal static string DescribeDays(IReadOnlyList<int> days)
    {
        var parts = new List<string>();
        var sorted = days.Distinct().OrderBy(d => d).ToList();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
                j++;
            parts.Add(j == i ? sorted[i].ToString() : $"{sorted[i]}–{sorted[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    // ----- zoom -----

    private void OnPlaybackZoomIn(object sender, RoutedEventArgs e) => PlaybackTimeline.ZoomBy(+1);

    private void OnPlaybackZoomOut(object sender, RoutedEventArgs e) => PlaybackTimeline.ZoomBy(-1);

    private void OnPlaybackZoomDay(object sender, RoutedEventArgs e) =>
        PlaybackTimeline.Window = TimelineWindow.Day(PlaybackDay);

    private void OnResultsToggle(object sender, RoutedEventArgs e) =>
        ResultsGrid.Visibility = ResultsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is RecordingSegment segment)
            _ = SeekPlaybackAsync(segment.Start);
    }

    // ----- playing -----

    /// <summary>
    /// Plays the camera from <paramref name="requested"/>: snapped to footage, opened over the
    /// web port, handed to the decoder. Whatever was playing is released after the new body is
    /// up — the player itself stops the old input when it is given the new media.
    /// </summary>
    private async Task SeekPlaybackAsync(DateTime requested)
    {
        if (_client is null || _clientCts is null || _libVlc is null || _playbackPlayer is null ||
            ChannelList.SelectedItem is not ChannelItem item)
        {
            SetStatus("Select a device and channel first.");
            return;
        }
        if (PlaybackClient is not { } playback)
        {
            SetStatus($"Playback is not implemented for {_client.Vendor} devices.");
            return;
        }

        var day = PlaybackDay;
        DateTime target = requested;
        if (_playbackDayLoaded)
        {
            var next = FootageCoverage.NextFootageAt(_playbackCoverage, requested);
            if (next is null)
            {
                StopPlayback(clearClock: false);
                SetStatus(_playbackCoverage.Count == 0
                    ? $"No recordings on {day:yyyy-MM-dd} for this camera."
                    : $"No footage after {requested:HH:mm:ss} on {day:yyyy-MM-dd}.");
                return;
            }
            target = next.Value;
        }
        var end = day.AddDays(1);
        if (target >= end)
            return;

        int gen = ++_playbackGen;
        int selection = _selectionGen;
        var stream = SelectedPlaybackStream;
        int channel = item.Channel.Id;
        var ct = _clientCts.Token;
        _playbackEnded = false;
        _playbackPaused = false;
        _playbackClock.Reanchor(target);
        PlaybackTimeline.Playhead = target;
        UpdatePlaybackButtons();
        SetStatus($"Opening footage at {target:HH:mm:ss} …");

        var startTask = playback.OpenPlaybackAsync(channel, target, end, stream, ct);
        _playbackStartTask = startTask;
        PlaybackStream? opened = null;
        ContainerPipe? pipe = null;
        try
        {
            opened = await startTask;
            if (gen != _playbackGen || selection != _selectionGen || _libVlc is null ||
                _playbackPlayer is null || _cleanupStarted)
            {
                _ = Task.Run(opened.Dispose);
                return;
            }

            Stream feed = opened.Body;
            if (opened.Container == PlaybackContainer.Dhav)
            {
                pipe = ContainerPipe.Start(opened.Body, "dhav", "mpegts");
                feed = pipe.Output;
            }

            var previousStream = _playbackStream;
            var previousPipe = _playbackPipe;
            _playbackStream = opened;
            _playbackPipe = pipe;

            // Non-seekable and of unknown length: the decoder plays it as it arrives.
            using var media = new Media(_libVlc, new StreamMediaInput(feed));
            AddLiveDecodeOptions(media);
            _playbackPlayer.Play(media);
            _playbackPlayer.SetRate(_playbackClock.Rate);
            UpdatePlaybackButtons();

            // Play has stopped the old input, so its body can go now — off the UI thread,
            // because closing a half-read HTTP response can wait on the socket.
            if (previousStream is not null || previousPipe is not null)
                _ = Task.Run(() =>
                {
                    previousPipe?.Dispose();
                    previousStream?.Dispose();
                });

            string route = opened.Container switch
            {
                PlaybackContainer.Dhav => $"web port {_currentDevice?.HttpPort} via ffmpeg",
                _ => $"web port {_currentDevice?.HttpPort}",
            };
            SetStatus($"Playing channel {channel} from {target:HH:mm:ss} ({stream}) over the {route} — no RTSP involved.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            pipe?.Dispose();
            opened?.Dispose();
            if (gen != _playbackGen)
                return;
            _playbackClock.Clear();
            PlaybackTimeline.Playhead = null;
            UpdatePlaybackButtons();
            SetStatus($"Playback failed: {Shorten(ex.Message)}");
        }
        finally
        {
            if (ReferenceEquals(startTask, _playbackStartTask))
                _playbackStartTask = null;
        }
    }

    private void OnPlaybackPlayPause(object sender, RoutedEventArgs e)
    {
        if (_playbackStream is null || _playbackPlayer is null)
        {
            // Nothing up: start at the playhead if there is one, else the day's first footage.
            var from = _playbackClock.Position
                ?? (_playbackCoverage.Count > 0 ? _playbackCoverage[0].Start : PlaybackDay);
            _ = SeekPlaybackAsync(from);
            return;
        }
        if (_playbackEnded)
        {
            if (_playbackClock.Position is DateTime at)
                _ = SeekPlaybackAsync(at);
            return;
        }
        _playbackPaused = !_playbackPaused;
        _playbackPlayer.SetPause(_playbackPaused);
        UpdatePlaybackButtons();
    }

    private void OnPlaybackStop(object sender, RoutedEventArgs e)
    {
        StopPlayback(clearClock: false);
        SetStatus("Playback stopped. The playhead stays where it was — ▶ Play resumes there.");
    }

    private void OnPlaybackSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        float rate = PlaybackRates[Math.Clamp(PlaybackSpeedCombo.SelectedIndex, 0, PlaybackRates.Length - 1)];
        _playbackClock.Rate = rate;
        if (_playbackStream is not null)
            _playbackPlayer?.SetRate(rate);
    }

    /// <summary>
    /// Ends the stream: the player off the UI thread (LibVLC's Stop blocks until its threads
    /// join), then the body and pipe behind it, in that order so nothing reads a closed stream.
    /// </summary>
    private void StopPlayback(bool clearClock)
    {
        _playbackGen++;
        var stream = _playbackStream;
        var pipe = _playbackPipe;
        _playbackStream = null;
        _playbackPipe = null;
        _playbackPaused = false;
        _playbackEnded = false;
        if (clearClock)
        {
            _playbackClock.Clear();
            PlaybackTimeline.Playhead = null;
        }
        UpdatePlaybackButtons();

        var player = _playbackPlayer;
        if (player is null && stream is null && pipe is null)
            return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (player is not null && !_cleanupStarted)
            {
                lock (_playerLock)
                {
                    if (!_playersDisposed)
                    {
                        try { player.Stop(); }
                        catch (ObjectDisposedException) { }
                    }
                }
            }
            pipe?.Dispose();
            stream?.Dispose();
        });
    }

    /// <summary>Shutdown-time teardown, waited on — before the players are stopped.</summary>
    private async Task DisposePlaybackAsync()
    {
        _playbackGen++;
        _playbackLoadGen++;
        _playbackTimer.Stop();
        if (_playbackStartTask is { } starting)
        {
            try { await starting; }
            catch { /* already reported by SeekPlaybackAsync */ }
        }
        var stream = _playbackStream;
        var pipe = _playbackPipe;
        _playbackStream = null;
        _playbackPipe = null;
        if (stream is not null || pipe is not null)
            await Task.Run(() =>
            {
                pipe?.Dispose();
                stream?.Dispose();
            });
    }

    // ----- the playhead -----

    private void UpdatePlayback()
    {
        if (_cleanupStarted)
        {
            _playbackTimer.Stop();
            return;
        }
        if (_playbackClock.Anchor is null)
        {
            PlaybackClockText.Text = "";
            return;
        }

        var player = _playbackPlayer;
        if (_playbackStream is not null && player is not null && !_playbackPaused)
            _playbackClock.Update(player.Time);
        var position = _playbackClock.Position;
        PlaybackTimeline.Playhead = position;

        string state = _playbackStream is null ? "stopped"
            : _playbackEnded ? "ended"
            : _playbackPaused ? "paused"
            : _playbackClock.Rate == 1f ? "playing" : $"playing {_playbackClock.Rate:0}×";
        PlaybackClockText.Text = position is DateTime p ? $"{p:yyyy-MM-dd HH:mm:ss}  {state}" : "";

        if (_playbackStream is null || _playbackPaused || position is not DateTime at)
            return;

        // A run of footage ended, or the decoder ran past what we know is recorded: move on to
        // the next run rather than sit on a frozen frame. Only when the day is loaded — without
        // the segment list there is nothing to skip to, and the recorder's body already skips.
        bool pastFootage = _playbackDayLoaded && !FootageCoverage.Contains(_playbackCoverage, at) &&
            at - _playbackClock.Anchor.Value > TimeSpan.FromSeconds(2);
        if (_playbackEnded || pastFootage)
        {
            var next = _playbackDayLoaded
                ? FootageCoverage.NextFootageAt(_playbackCoverage, at.AddSeconds(1))
                : null;
            if (next is DateTime n && n < PlaybackDay.AddDays(1))
            {
                _ = SeekPlaybackAsync(n);
            }
            else if (_playbackEnded)
            {
                StopPlayback(clearClock: false);
                SetStatus($"End of the footage on {PlaybackDay:yyyy-MM-dd} for this camera.");
            }
        }
    }

    private void UpdatePlaybackButtons()
    {
        PlaybackPlayPauseButton.Content = _playbackStream is null || _playbackEnded ? "▶ Play"
            : _playbackPaused ? "▶ Resume"
            : "⏸ Pause";
    }

    private void UpdatePlaybackSelectionLabel()
    {
        if (PlaybackTimeline.Selection is { } sel)
        {
            var length = sel.End - sel.Start;
            PlaybackSelectionText.Text = $"Selected {sel.Start:HH:mm:ss} → {sel.End:HH:mm:ss} ({(long)length.TotalMinutes} min {length.Seconds} s)";
        }
        else
        {
            PlaybackSelectionText.Text = "";
        }
    }

    // ----- export -----

    private async void OnExportSelection(object sender, RoutedEventArgs e)
    {
        if (_client is null || ChannelList.SelectedItem is not ChannelItem item)
        {
            SetStatus("Select a device and channel first.");
            return;
        }
        if (PlaybackTimeline.Selection is not { } sel || sel.End <= sel.Start)
        {
            SetStatus("Drag across the timeline to select the range to export.");
            return;
        }
        var client = _client;
        int channel = item.Channel.Id;
        await RunDownloadAsync($"ch{channel}_{sel.Start:yyyyMMdd_HHmmss}-{sel.End:HHmmss}",
            client.Vendor, null, (path, progress, ct) =>
                client.DownloadAsync(channel, sel.Start, sel.End, path, progress, ct));
    }

    private async void OnDownloadSegment(object sender, RoutedEventArgs e)
    {
        if (_client is null || ResultsGrid.SelectedItem is not RecordingSegment segment)
        {
            SetStatus("Open the segment list and select a row first.");
            return;
        }
        var client = _client;
        await RunDownloadAsync($"ch{segment.Channel}_{segment.Start:yyyyMMdd_HHmmss}",
            client.Vendor, segment.SizeBytes, (path, progress, ct) =>
                client.DownloadSegmentAsync(segment, path, progress, ct));
    }
}
