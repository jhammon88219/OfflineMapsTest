using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OfflineMapsTest.Models;
using OfflineMapsTest.Services;

namespace OfflineMapsTest.ViewModels
{
	// RadarViewModel (partial): the loop engine — site load, live-frame poll, playback, refresh, incremental reload, past-event load.
	public sealed partial class RadarViewModel
	{
		// Appends one free-form radar diagnostic note. High-value events (session start, frame
		// timings, live polls, frame sources) use the typed RadarDiagnostics methods directly so
		// they also feed the rolling report; this is for the incidental lines.
		private static void Diag(string message) => Services.RadarDiagnostics.Log("vm", "note", ("msg", message));

		// Maps a loaded volume to its on-disk .V06 (the radarlevel2 host serves CacheDirectory),
		// so a suspect frame's source can be quarantined.
		private string FrameCacheFile(Models.RadarVolume v) =>
			System.IO.Path.Combine(_radarService.CacheDirectory, v.LocalUrl.Substring(v.LocalUrl.LastIndexOf('/') + 1));

		// Loads a fresh loop for the site (or clears for "None"): recenters immediately, shows
		// the newest frame first, then backfills older frames; also starts the playback and
		// auto-refresh loops tied to this selection. Cancels the previous selection's work.
		private async Task StartRadarLoopAsync(RadarSite? site)
		{
			Services.RadarDiagnostics.BeginSession(site?.Id);
			_loopCts?.Cancel();
			_loopCts = null;
			IsPlaying = false;
			SetLoopEngaged(false); // a freshly (re)loaded loop is stopped -> Stop disabled
			IsLoopReady = false;

			// Highlight the selected site marker (null clears it).
			if (_isMapReady)
			{
				await _mapService.SetSelectedRadarSiteAsync(site?.Id);
			}

			if (site is null)
			{
				ResetFrameState();
				OnPropertyChanged(nameof(MaxFrameIndex));
				OnPropertyChanged(nameof(CurrentFrameTimeText));
				RaiseRadarReadout();
				IsInspecting = false; // no loop to inspect — drop the crosshair + marker
				if (_isMapReady)
				{
					await _mapService.ClearRadarAsync();
					await _mapService.SetRadarSweepAsync(0); // back to the free-running sweep
				}
				return;
			}

			// Start the load timer for this click (frozen once the initial load fully renders).
			_loopClickAt = DateTimeOffset.UtcNow;
			_firstFrameElapsed = null;
			_allFramesElapsed = null;
			_initialLoadDone = false;
			_loadInProgress = true;
			// Until THIS loop has actually begun (BeginRadarLoopAsync below, which bumps the JS
			// loop token), any frame-ready is a leftover from the previous selection still draining
			// through the worker — the JS token only drops stale frames AFTER the bump, so the gap
			// during the keys fetch leaks them. Ignore them so they don't pollute first-frame timing
			// or the ready count of the new session.
			_loopRenderBegun = false;
			_liveModeText = null; // forget the previous site's mode; the new site's poll re-sets it

			// Note: no flyTo — load the radar at the user's current view; they pan/zoom freely.
			var cts = new CancellationTokenSource();
			_loopCts = cts;

			await LoadLoopAsync(site, cts.Token);

			// The frame set (incl. any live frame) is now final; record "all frames" timing once
			// every frame has reported ready (or as they finish, via OnRadarFrameReady).
			_loadInProgress = false;
			MaybeRecordAllFramesLoaded();

			_ = RunPlaybackAsync(cts.Token);
			_ = RunRefreshAsync(site, cts.Token);
			_ = RunLiveFrameRefreshAsync(site, cts.Token);
			_ = RunDebugTickAsync(cts.Token);
		}

		// Zeroes all per-loop frame + live-poll state. Shared by the clear path (StartRadarLoopAsync)
		// and the Past Event Viewer's site-change clear (SelectPastSiteAsync). Callers raise the
		// relevant PropertyChanged / RaiseRadarReadout afterwards.
		private void ResetFrameState()
		{
			_frameCount = 0;
			_archiveCount = 0;
			_hasLiveFrame = false;
			_liveFrame = null;
			_liveModeText = null;
			_frameTimes = Array.Empty<DateTimeOffset?>();
			_frameModes = Array.Empty<string?>();
			Segments.Clear();
			_loadedNewestKey = null;
			_loadedKeys = Array.Empty<string>();
			_lastLivePollAt = null;
			_lastLivePollResult = null;
			_nextLivePollAt = null;
			_livePollCycleStart = null;
			_lastLiveError = null;
			_loopClickAt = null;
			_firstFrameElapsed = null;
			_allFramesElapsed = null;
		}

		/// <summary>
		/// Hard reset of the current loop: cancels the in-flight load/playback/refresh, dumps every
		/// frame, and reloads from scratch (re-list keys, re-decode, re-render — recovers a glitched
		/// loop without waiting for the ~5-min auto-refresh). No-op when no site is selected. Reuses
		/// the on-disk volume cache, so it's fast; it re-renders rather than re-downloading bytes.
		/// </summary>
		public void ResetRadarLoop()
		{
			if (_selectedRadarOption?.Site is not { } site)
			{
				return;
			}

			Diag("manual loop reset");
			_ = StartRadarLoopAsync(site);
		}

		// Past mode: a site pick clears any loaded replay and highlights the new site's marker, but
		// starts NOTHING — the user sets a window and hits Load. (Mirrors StartRadarLoopAsync's clear
		// path but keeps the marker on the chosen site so it reads as "armed" and runs no live loop.)
		private async Task SelectPastSiteAsync(RadarSite? site)
		{
			_loopCts?.Cancel();
			_loopCts = null;
			IsPlaying = false;
			SetLoopEngaged(false);
			IsLoopReady = false;
			IsInspecting = false;
			Services.RadarDiagnostics.BeginSession(null); // close any open session; Load opens the replay one
			ResetFrameState();
			OnPropertyChanged(nameof(MaxFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameTimeText));
			RaiseRadarReadout();
			if (_isMapReady)
			{
				await _mapService.SetSelectedRadarSiteAsync(site?.Id);
				await _mapService.ClearRadarAsync();
				await _mapService.SetRadarSweepAsync(0); // no live sweep in replay
			}
		}

		/// <summary>
		/// Loads the historical loop for the selected site over the chosen window (the Load button).
		/// Lists the archive volumes in the window, builds the loop with the same machinery as the
		/// live path, then starts playback — but with NO live poll and NO auto-refresh.
		/// </summary>
		public async Task<bool> LoadSelectedPastEventAsync()
		{
			if (!_isPastEventMode)
			{
				return false;
			}

			// Build the local date from the Year/Month/Day combos; clamp the day to the month's length
			// (so e.g. day 31 in a 30-day month just uses the 30th instead of throwing). Combine with
			// the time-of-day, then convert to UTC for the bucket query (DST-correct for that date).
			var year = PastEventYearOptions[_pastEventYearIndex];
			var month = _pastEventMonthIndex + 1;
			var day = Math.Min(_pastEventDayIndex + 1, DateTime.DaysInMonth(year, month));
			var localMidnight = new DateTimeOffset(year, month, day, 0, 0, 0,
				TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day)));
			var localStart = localMidnight + _pastEventTime;
			var startUtc = localStart.ToUniversalTime();
			var endUtc = startUtc.AddMinutes(PastEventMinutesByIndex[_pastEventDurationIndex]);

			// Window is now set — gray out sites with no data for this date (proactive availability).
			// Best-effort + non-blocking so it never delays the actual load.
			_ = ApplyPastAvailabilityAsync(startUtc, endUtc);

			if (_selectedRadarOption?.Site is not { } site)
			{
				// No site yet: just ARM the window (date/time/range) so any site you click next loads it,
				// and report success so the flyout closes and you can go site-surfing on the map.
				_pastWindowLoaded = true;
				PastEventStatus = "Window set — click a radar site on the map to load it.";
				return true;
			}

			// Highlight the loading site's on-map marker (deselecting any prior one). The not-armed path
			// does this via SelectPastSiteAsync; the armed "click another site" path comes straight here,
			// so set it here too or the previous site's marker stays lit.
			if (_isMapReady)
			{
				await _mapService.SetSelectedRadarSiteAsync(site.Id);
			}

			PastEventStatus = "Loading…";
			_loopCts?.Cancel();
			var cts = new CancellationTokenSource();
			_loopCts = cts;

			IReadOnlyList<string> keys;
			try
			{
				keys = await _radarService.GetKeysForWindowAsync(site, startUtc, endUtc, cts.Token);
			}
			catch (OperationCanceledException)
			{
				return false;
			}
			catch (Exception ex)
			{
				PastEventStatus = "Couldn't list volumes: " + ex.Message;
				return false;
			}

			if (cts.Token.IsCancellationRequested || !ReferenceEquals(_selectedRadarOption?.Site, site))
			{
				return false;
			}
			if (keys.Count == 0)
			{
				PastEventStatus = $"No {site.Id} data found for {localStart:MMM d, h:mm tt}.";
				return false;
			}
			// More volumes than the cap → evenly subsample across the whole window (first + last kept),
			// so a long duration becomes an overview rather than only the first chunk.
			var sampled = false;
			if (keys.Count > PastEventMaxFrames)
			{
				var pick = new List<string>(PastEventMaxFrames);
				for (var i = 0; i < PastEventMaxFrames; i++)
				{
					var idx = (int)Math.Round((double)i * (keys.Count - 1) / (PastEventMaxFrames - 1));
					pick.Add(keys[idx]);
				}
				keys = pick.Distinct().ToList();
				sampled = true;
			}

			await LoadPastLoopAsync(site, keys, startUtc, cts.Token);
			if (cts.Token.IsCancellationRequested)
			{
				return false;
			}

			MaybeRecordAllFramesLoaded();
			_ = RunPlaybackAsync(cts.Token);
			_ = RunDebugTickAsync(cts.Token);

			PastEventStatus = $"Loaded {keys.Count} frames{(sampled ? " (sampled)" : "")} · " +
				$"{localStart:MMM d, h:mm tt} +{PastEventDurationOptions[_pastEventDurationIndex]}";
			_pastWindowLoaded = true;
			return true;
		}

		// Builds the replay loop from the given keys — the live-free counterpart of LoadLoopCoreAsync.
		private async Task LoadPastLoopAsync(RadarSite site, IReadOnlyList<string> keys, DateTimeOffset startUtc, CancellationToken ct)
		{
			try
			{
				await _loopGate.WaitAsync(ct);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				Services.RadarDiagnostics.BeginSession(site.Id);
				Services.RadarDiagnostics.Log("vm", "replay.load",
					("site", site.Id), ("startZ", startUtc.ToString("O")), ("frames", keys.Count));

				_loopClickAt = DateTimeOffset.UtcNow;
				_firstFrameElapsed = null;
				_allFramesElapsed = null;
				_initialLoadDone = false;
				_loadInProgress = true;
				_loopRenderBegun = false;

				_archiveCount = keys.Count;
				_frameCount = keys.Count;
				_liveFrame = null;
				_hasLiveFrame = false;
				_liveModeText = null;
				_frameTimes = new DateTimeOffset?[_frameCount];
				_frameModes = new string?[_frameCount];
				RebuildSegments(_frameCount); // empty scrubber cells; they light as frames decode
				_readyCount = 0;
				_loadedKeys = keys.ToArray();
				_loadedNewestKey = keys[^1];
				IsLoopReady = false;
				_currentFrameIndex = 0; // start at the beginning of the event so play moves forward
				OnPropertyChanged(nameof(MaxFrameIndex));
				OnPropertyChanged(nameof(CurrentFrameIndex));
				OnPropertyChanged(nameof(CurrentFrameTimeText));
				OnPropertyChanged(nameof(RadarLoadingText));

				if (_isMapReady)
				{
					await _mapService.BeginRadarLoopAsync(site);
				}
				_loopRenderBegun = true;

				// Oldest frame first (it's adopted + shown immediately), then the rest in parallel (bounded).
				await EnsureAndAddFrameAsync(site, keys, 0, ct);
				await BackfillFramesAsync(site, keys, 1, keys.Count, ct);
				_loadInProgress = false;
			}
			finally
			{
				_loopGate.Release();
			}
		}

		// Ticks the debug card once a second so its ages stay current while a loop is active.
		private async Task RunDebugTickAsync(CancellationToken ct)
		{
			try
			{
				using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
				while (await timer.WaitForNextTickAsync(ct))
				{
					RaiseRadarReadout();
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed or app shutting down.
			}
		}

		// Lists the recent archive volumes, fetches the near-real-time live frame from the
		// chunks bucket, begins a loop, shows the newest, then backfills the rest. The live
		// frame (when available) is appended as an extra newest frame at index _archiveCount.
		private async Task LoadLoopAsync(RadarSite site, CancellationToken ct)
		{
			try
			{
				await _loopGate.WaitAsync(ct);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				await LoadLoopCoreAsync(site, ct);
			}
			finally
			{
				_loopGate.Release();
			}
		}

		// The actual load, run under _loopGate (see LoadLoopAsync). Ends by fetching the live
		// frame inline so the whole sequence is atomic w.r.t. the live poll.
		private async Task LoadLoopCoreAsync(RadarSite site, CancellationToken ct)
		{
			IReadOnlyList<string> keys;
			try
			{
				keys = await _radarService.GetRecentKeysAsync(site, LoopLength, ct);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch
			{
				return;
			}

			if (ct.IsCancellationRequested || keys.Count == 0 || !ReferenceEquals(_selectedRadarOption?.Site, site))
			{
				Services.RadarDiagnostics.Log("vm", "loop.abort", ("keys", keys.Count), ("cancelled", ct.IsCancellationRequested));
				return;
			}

			Services.RadarDiagnostics.Log("vm", "loop.keys", ("count", keys.Count), ("newest", keys[^1]));

			// Size the loop for the archive frames only; the live frame (if it turns out to be
			// fresher) is appended afterwards by RefreshLiveFrameAsync. Loading archive first
			// also means the newest archive frame paints immediately, before the chunks fetch.
			_archiveCount = keys.Count;
			_liveFrame = null;
			_hasLiveFrame = false;
			_frameCount = _archiveCount;
			_frameTimes = new DateTimeOffset?[_frameCount];
			_frameModes = new string?[_frameCount];
			RebuildSegments(_frameCount); // empty scrubber cells; they light as frames decode
			_readyCount = 0;
			_loadedNewestKey = keys[_archiveCount - 1]; // archive newest drives the 5-min reload
			_loadedKeys = keys.ToArray();               // baseline for the next incremental refresh
			IsLoopReady = false;
			_currentFrameIndex = _frameCount - 1; // newest archive frame
			OnPropertyChanged(nameof(MaxFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameTimeText));
			OnPropertyChanged(nameof(RadarLoadingText));

			if (_isMapReady)
			{
				await _mapService.BeginRadarLoopAsync(site);
			}

			// The loop (and its JS token) is now (re)started — frame-ready events from here on
			// belong to this selection, so first-frame timing can trust them.
			_loopRenderBegun = true;

			// Newest archive frame first (immediate display).
			await EnsureAndAddFrameAsync(site, keys, _archiveCount - 1, ct);

			// Backfill the older archive frames IN PARALLEL (bounded) — each frame's cost is a full-
			// volume AWS download + bzip2 tilt extraction, so running them concurrently is the main lever
			// for "load all back frames faster". This now runs BEFORE the live poll: the chunks-bucket
			// live frame is slow to build (~8-12 s — dozens/hundreds of chunks to download + bzip2-decode),
			// and awaiting it first BLOCKED the fast parallel backfill, which read as a long "stall on
			// frame 1". Filling the visible loop first, then appending the live frame, is far snappier.
			await BackfillFramesAsync(site, keys, 0, _archiveCount - 1, ct);

			// Now pull the live (chunks) frame + scan mode; it appends at index _archiveCount when fresher
			// than the archive newest, and carries the scan-mode text for the card.
			await RefreshLiveFrameAsync(site, ct);
		}

		// Fetches the live (chunks) frame and, when it's newer than what's shown, applies it —
		// appending a new trailing frame or updating the existing live slot in place. Records the
		// outcome for the debug card. Best-effort: a null result just leaves the archive newest.
		private async Task RefreshLiveFrameAsync(RadarSite site, CancellationToken ct)
		{
			Models.RadarVolume? live;
			try
			{
				_lastLiveError = null;
				live = await _radarService.GetLiveFrameAsync(site, ct);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_lastLiveError = ex.Message;
				live = null;
			}

			RecordLivePoll(live);

			if (live is null || ct.IsCancellationRequested || !ReferenceEquals(_selectedRadarOption?.Site, site))
			{
				return;
			}

			await ApplyLiveFrameAsync(live);
		}

		// Applies a live frame: updates the existing trailing live slot in place, or appends a new
		// one — but only when strictly newer than the current live / archive newest, so a stale
		// chunks volume can never override fresh data (the bug behind KVNX/KFDR old frames).
		private async Task ApplyLiveFrameAsync(Models.RadarVolume live)
		{
			if (_hasLiveFrame)
			{
				if (_liveFrame is not null && live.VolumeTime <= _liveFrame.VolumeTime)
				{
					Services.RadarDiagnostics.Log("vm", "live.apply", ("action", "skip"),
						("reason", $"not newer than current live ({live.VolumeTime:HH:mm:ss}Z <= {_liveFrame.VolumeTime:HH:mm:ss}Z)"));
					return;
				}

				_liveFrame = live;
				if (_archiveCount < _frameTimes.Length)
				{
					_frameTimes[_archiveCount] = live.VolumeTime;
				}
				if (_archiveCount < _frameModes.Length)
				{
					_frameModes[_archiveCount] = live.ModeText;
				}
				Services.RadarDiagnostics.Log("vm", "live.apply", ("action", "update"),
					("idx", _archiveCount), ("volZ", live.VolumeTime.ToUniversalTime().ToString("HH:mm:ss")));
				Services.RadarDiagnostics.RegisterFrameSource(_archiveCount, "live", FrameCacheFile(live), live.VolumeTime);
				if (_isMapReady)
				{
					await _mapService.AddRadarFrameAsync(live.LocalUrl, _archiveCount);
					await _mapService.PulseRadarSweepAsync(); // new frame landed → one sweep pulse
				}
				if (_currentFrameIndex == _archiveCount)
				{
					OnPropertyChanged(nameof(CurrentFrameTimeText));
				}
				RaiseRadarReadout();
				return;
			}

			// No live slot yet: only append if the chunks volume is newer than the archive newest.
			var archiveNewest = _archiveCount > 0 && _archiveCount - 1 < _frameTimes.Length
				? _frameTimes[_archiveCount - 1]
				: null;
			if (archiveNewest is { } an && live.VolumeTime <= an)
			{
				Services.RadarDiagnostics.Log("vm", "live.apply", ("action", "skip"),
					("reason", $"not newer than archive ({live.VolumeTime:HH:mm:ss}Z <= {an:HH:mm:ss}Z)"));
				return;
			}

			var grown = new DateTimeOffset?[_archiveCount + 1];
			Array.Copy(_frameTimes, grown, _archiveCount);
			grown[_archiveCount] = live.VolumeTime;
			_frameTimes = grown;
			var grownModes = new string?[_archiveCount + 1];
			Array.Copy(_frameModes, grownModes, Math.Min(_archiveCount, _frameModes.Length));
			grownModes[_archiveCount] = live.ModeText;
			_frameModes = grownModes;
			// Grow the scrubber to include the live frame's cell (keeps Segments.Count == _frameCount, so
			// the playhead — which divides the track by Segments.Count — stays aligned). OnRadarFrameReady
			// lights it once it decodes.
			if (Segments.Count == _archiveCount)
			{
				Segments.Add(new RadarFrameSegment());
			}
			_liveFrame = live;
			_hasLiveFrame = true;
			_frameCount = _archiveCount + 1;
			_currentFrameIndex = _frameCount - 1; // show the live frame as the new newest
			Services.RadarDiagnostics.Log("vm", "live.apply", ("action", "append"),
				("idx", _archiveCount), ("volZ", live.VolumeTime.ToUniversalTime().ToString("HH:mm:ss")),
				("frames", _frameCount));
			Services.RadarDiagnostics.RegisterFrameSource(_archiveCount, "live", FrameCacheFile(live), live.VolumeTime);
			if (_loopClickAt is { } liveClick)
			{
				Services.RadarDiagnostics.Timing("live", (DateTimeOffset.UtcNow - liveClick).TotalSeconds);
			}
			OnPropertyChanged(nameof(MaxFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameTimeText));
			RaiseRadarReadout();

			if (_isMapReady)
			{
				await _mapService.AddRadarFrameAsync(live.LocalUrl, _archiveCount);
				await _mapService.ShowRadarFrameAsync(_archiveCount);
				await _mapService.PulseRadarSweepAsync(); // first live frame landed → one sweep pulse
			}
		}

		// Records the latest live-frame poll outcome for the debug card.
		private void RecordLivePoll(Models.RadarVolume? live)
		{
			_lastLivePollAt = DateTimeOffset.Now;
			_lastLivePollResult = _lastLiveError is not null
				? $"error: {_lastLiveError}"
				: live is null
					? "null (no fresh tilt; using archive)"
					: $"ok · {live.VolumeTime.ToUniversalTime():HH:mm:ss}Z";
			// The decoded volume carries the scan mode regardless of whether it's fresh enough to
			// append as a new frame — capture it so the mode shows even for a stale/offline site.
			if (live?.ModeText is { } mode)
			{
				_liveModeText = mode;
			}
			Services.RadarDiagnostics.LivePoll(_lastLivePollResult, live?.VolumeTime, live?.ModeText);
			RaiseRadarReadout();
		}

		private async Task EnsureAndAddFrameAsync(RadarSite site, IReadOnlyList<string> keys, int index, CancellationToken ct)
		{
			try
			{
				var volume = await _radarService.EnsureCachedAsync(site, keys[index], ct);
				if (volume is null || ct.IsCancellationRequested || !ReferenceEquals(_selectedRadarOption?.Site, site))
				{
					return;
				}

				_frameTimes[index] = volume.VolumeTime;
				if (index < _frameModes.Length) _frameModes[index] = volume.ModeText;
				Services.RadarDiagnostics.RegisterFrameSource(index, "archive", FrameCacheFile(volume), volume.VolumeTime);
				if (_isMapReady)
				{
					await _mapService.AddRadarFrameAsync(volume.LocalUrl, index);
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed; stop.
			}
			catch (Exception ex)
			{
				// Skip a bad frame; the rest of the loop still loads.
				Services.RadarDiagnostics.Log("vm", "frame.fail", ("idx", index), ("error", ex.Message));
			}
		}

		// How many archive volumes to download + extract concurrently during backfill. The per-frame cost
		// is network + bzip2 extraction; 6 keeps AWS + the threadpool busy without hammering either.
		private const int MaxParallelBackfill = 6;

		// Loads a run of frames [startInclusive, endExclusive) with BOUNDED PARALLELISM. The per-frame
		// cost is a network download + bzip2 tilt extraction (both off the UI thread), so overlapping
		// them cuts a ~10-frame backfill from tens of seconds to a few. Concurrency is safe: each frame
		// writes its own index and caches to its own file; only the light AddRadarFrameAsync posts
		// resume on the UI thread (WebView2 is UI-affine), which serializes them naturally. Runs under
		// the caller's _loopGate, so no live poll can interleave.
		private async Task BackfillFramesAsync(RadarSite site, IReadOnlyList<string> keys, int startInclusive, int endExclusive, CancellationToken ct)
		{
			using var gate = new SemaphoreSlim(MaxParallelBackfill);
			var tasks = new List<Task>();
			for (var i = startInclusive; i < endExclusive && !ct.IsCancellationRequested; i++)
			{
				try
				{
					await gate.WaitAsync(ct); // cap frames in flight
				}
				catch (OperationCanceledException)
				{
					break;
				}
				var index = i;
				tasks.Add(BackfillOneAsync(index));
			}
			await Task.WhenAll(tasks);

			async Task BackfillOneAsync(int index)
			{
				try
				{
					await EnsureAndAddFrameAsync(site, keys, index, ct);
				}
				finally
				{
					gate.Release();
				}
			}
		}

		/// <summary>Called by the view when a radar site marker is clicked (toggles selection).</summary>
		public void OnRadarSiteClicked(string? id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return;
			}

			var option = RadarOptions.FirstOrDefault(o => o.Site?.Id == id);
			if (option is null)
			{
				return;
			}

			// Clicking the already-selected site clears it; otherwise select it.
			SelectedRadarOption = ReferenceEquals(option, _selectedRadarOption) ? RadarOptions[0] : option;
		}

		/// <summary>Called by the view when the WebView reports a loop frame finished decoding.</summary>
		public void OnRadarFrameReady(int index, bool hasData)
		{
			// Drop frame-ready events that arrive before this selection's loop has begun — they're
			// stale leftovers from the previous site draining through the worker (see _loopRenderBegun).
			if (!_loopRenderBegun)
			{
				return;
			}

			_readyCount++;
			if (index >= 0 && index < Segments.Count)
			{
				// Mark decoded, then derive the displayed readiness for the active product (Velocity still
				// needs its dealiased geometry, so the cell may stay "loading" until the build reaches it).
				Segments[index].IsDecoded = true;
				Segments[index].IsReady = IsFrameDisplayReady(index);
			}
			Services.RadarDiagnostics.FrameReady(index, hasData, _readyCount, _frameCount);

			// First-frame timing: the moment the first frame of this click is decoded + shown.
			if (!_initialLoadDone && _firstFrameElapsed is null && _loopClickAt is { } click)
			{
				_firstFrameElapsed = DateTimeOffset.UtcNow - click;
				Services.RadarDiagnostics.Timing("first", _firstFrameElapsed.Value.TotalSeconds);
				RaiseRadarReadout();
			}

			OnPropertyChanged(nameof(CurrentFrameTimeText));
			if (_readyCount >= _frameCount && _frameCount > 0)
			{
				IsLoopReady = true;
				OnPropertyChanged(nameof(CurrentFrameTimeText));
			}

			MaybeRecordAllFramesLoaded();
		}

		// Records the "all frames loaded + rendered" timing once, after the initial load has
		// settled on its final frame count (so the live frame is included) and every frame has
		// reported ready. Frozen by _initialLoadDone so later live refreshes don't overwrite it.
		private void MaybeRecordAllFramesLoaded()
		{
			if (_initialLoadDone || _loadInProgress || _frameCount == 0 || _readyCount < _frameCount)
			{
				return;
			}
			if (_loopClickAt is { } click)
			{
				_allFramesElapsed = DateTimeOffset.UtcNow - click;
				Services.RadarDiagnostics.Timing("all", _allFramesElapsed.Value.TotalSeconds);
				RaiseRadarReadout();
			}
			_initialLoadDone = true;
		}

		// Advances the loop while playing + ready (~0.5s/frame, with a brief dwell on newest).
		private async Task RunPlaybackAsync(CancellationToken ct)
		{
			try
			{
				var dwell = 0;
				while (!ct.IsCancellationRequested)
				{
					// Variable per-frame delay so the playback-speed combo applies immediately.
					await Task.Delay(PlaybackIntervalMs, ct);
					if (!_isPlaying || !_isLoopReady || _frameCount == 0)
					{
						continue;
					}

					// Pause a couple of ticks on the newest frame before looping back.
					if (_currentFrameIndex >= _frameCount - 1 && dwell < 2)
					{
						dwell++;
						continue;
					}

					// Hold at the built frontier: don't advance onto a frame whose velocity is still being
					// dealiased in the background (that would flash blank / stall ~1.5 s mid-playback). The
					// upgrade queue builds forward from the playhead, so playback resumes on its own as each
					// next frame becomes ready. Reflectivity/CC are always ready, so this never holds there.
					var next = (_currentFrameIndex + 1) % _frameCount;
					if (!IsFrameDisplayReady(next))
					{
						continue;
					}

					dwell = 0;
					CurrentFrameIndex = next;
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed or app shutting down.
			}
		}

		// Every ~5 min, if a newer volume exists, reloads the loop (keeps the hour current).
		private async Task RunRefreshAsync(RadarSite site, CancellationToken ct)
		{
			try
			{
				using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
				while (await timer.WaitForNextTickAsync(ct))
				{
					if (!ReferenceEquals(_selectedRadarOption?.Site, site))
					{
						return;
					}

					IReadOnlyList<string> keys;
					try
					{
						keys = await _radarService.GetRecentKeysAsync(site, LoopLength, ct);
					}
					catch (OperationCanceledException)
					{
						return;
					}
					catch
					{
						continue;
					}

					if (keys.Count > 0 && keys[keys.Count - 1] != _loadedNewestKey)
					{
						Services.RadarDiagnostics.Log("vm", "refresh.archive", ("newKey", keys[^1]), ("oldKey", _loadedNewestKey));
						// Predictive prefetch: pull the new volume's .V06 to disk FIRST, OFF the loop's
						// critical path (no _loopGate, no loop-state changes) — so the download (the slow
						// part) happens while the loop stays fully live, and the incremental fold-in below
						// is decode-only (its EnsureCachedAsync becomes an instant disk hit). Without this,
						// the fold ran the download while HOLDING _loopGate, stalling the live-frame poll.
						await PrefetchArchiveFramesAsync(site, keys, ct);
						// Incrementally fold in the new volume (reuse the unchanged decoded frames, no
						// layer teardown) instead of a full rebuild — that rebuild blanked the radar for
						// ~1.5-6 s and flashed a stale archive frame every 5 min.
						await ReloadLoopIncrementalAsync(site, keys, ct);
					}
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed or app shutting down.
			}
		}

		// Folds a newly-arrived archive volume into the loop WITHOUT a teardown: it diffs the new key
		// list against the loaded one, reindexes (in JS) the frames whose volumes are unchanged so
		// their decoded geometry is reused, and decodes only the genuinely-new volume(s). The live
		// frame is carried over too. Because the layer is never removed and the on-screen frame stays
		// up, the periodic reload no longer blanks the radar or flashes a stale archive frame.
		// Serialized under _loopGate against the live poll, like the full load.
		private async Task ReloadLoopIncrementalAsync(RadarSite site, IReadOnlyList<string> newKeys, CancellationToken ct)
		{
			try
			{
				await _loopGate.WaitAsync(ct);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				if (newKeys.Count == 0 || !ReferenceEquals(_selectedRadarOption?.Site, site))
				{
					return;
				}

				var oldFrameTimes = _frameTimes;
				var oldFrameModes = _frameModes;
				var oldReady = new bool[Segments.Count]; // reused frames keep their DECODE state (relit per product)
				for (var i = 0; i < oldReady.Length; i++) oldReady[i] = Segments[i].IsDecoded;
				var oldArchiveCount = _archiveCount;
				var oldFrameCount = _frameCount;
				var hadLive = _hasLiveFrame;
				var oldCurrent = _currentFrameIndex;
				var wasNewest = oldCurrent == oldFrameCount - 1; // user following the latest frame

				// First old index for each archive key (the loop has no duplicate volumes in practice).
				var oldIndexByKey = new Dictionary<string, int>(oldArchiveCount);
				for (var i = 0; i < _loadedKeys.Length && i < oldArchiveCount; i++)
				{
					oldIndexByKey.TryAdd(_loadedKeys[i], i);
				}

				var newArchiveCount = newKeys.Count;
				var newFrameCount = newArchiveCount + (hadLive ? 1 : 0);
				var newTimes = new DateTimeOffset?[newFrameCount];
				var newModes = new string?[newFrameCount];       // scan mode, reused in lockstep with times
				var newReady = new bool[newFrameCount];           // lit scrubber cells, reused in lockstep
				var mapping = new List<int[]>(newFrameCount);     // [fromIndex, toIndex] reuses
				var newIndices = new List<int>();                  // new archive slots needing decode

				for (var j = 0; j < newArchiveCount; j++)
				{
					if (oldIndexByKey.TryGetValue(newKeys[j], out var oi) && oi < oldFrameTimes.Length)
					{
						mapping.Add(new[] { oi, j });
						newTimes[j] = oldFrameTimes[oi];
						if (oi < oldFrameModes.Length) newModes[j] = oldFrameModes[oi];
						if (oi < oldReady.Length) newReady[j] = oldReady[oi];
					}
					else
					{
						newIndices.Add(j); // brand-new volume -> decode
					}
				}

				// The live (chunks) frame persists across an archive reload — carry it to the new top.
				if (hadLive && oldArchiveCount < oldFrameTimes.Length)
				{
					mapping.Add(new[] { oldArchiveCount, newArchiveCount });
					newTimes[newArchiveCount] = oldFrameTimes[oldArchiveCount];
					if (oldArchiveCount < oldFrameModes.Length) newModes[newArchiveCount] = oldFrameModes[oldArchiveCount];
					if (oldArchiveCount < oldReady.Length) newReady[newArchiveCount] = oldReady[oldArchiveCount];
				}

				// Where the displayed frame lands after the reindex (so we can keep it on screen).
				var newCurrent = -1;
				foreach (var m in mapping)
				{
					if (m[0] == oldCurrent) { newCurrent = m[1]; break; }
				}

				// Commit VM state. Don't touch IsLoopReady: most frames are already decoded, so the
				// loop stays "ready" (scrubber/playback uninterrupted). _readyCount = reused count;
				// the new frames bring it back up to newFrameCount as they decode.
				_loadedKeys = newKeys.ToArray();
				_loadedNewestKey = newKeys[^1];
				_archiveCount = newArchiveCount;
				_frameCount = newFrameCount;
				_frameTimes = newTimes;
				_frameModes = newModes;
				RebuildSegments(newFrameCount, newReady); // reindex scrubber cells; reused frames stay lit
				_readyCount = mapping.Count;
				_currentFrameIndex = wasNewest ? newFrameCount - 1
					: newCurrent >= 0 ? newCurrent
					: newFrameCount - 1;

				Services.RadarDiagnostics.Log("vm", "refresh.incremental",
					("reused", mapping.Count), ("new", newIndices.Count),
					("frames", newFrameCount), ("newest", newKeys[^1]));

				if (_isMapReady)
				{
					var mappingJson = System.Text.Json.JsonSerializer.Serialize(mapping);
					await _mapService.RemapRadarFramesAsync(newFrameCount, mappingJson);
					// Target the desired frame: if undecoded (a just-arrived newest), JS records it as
					// pending and keeps the current frame on screen until it decodes (no blank).
					await _mapService.ShowRadarFrameAsync(_currentFrameIndex);
				}

				OnPropertyChanged(nameof(MaxFrameIndex));
				OnPropertyChanged(nameof(CurrentFrameIndex));
				OnPropertyChanged(nameof(CurrentFrameTimeText));
				RaiseRadarReadout();

				// Decode only the genuinely-new volumes (newest-first so the top updates first).
				for (var k = newIndices.Count - 1; k >= 0 && !ct.IsCancellationRequested; k--)
				{
					await EnsureAndAddFrameAsync(site, newKeys, newIndices[k], ct);
				}
			}
			finally
			{
				_loopGate.Release();
			}
		}

		// Predictive prefetch: warm the on-disk .V06 cache for the volumes a reload is about to fold in,
		// WITHOUT _loopGate and WITHOUT touching loop state — so the download happens with the loop fully
		// live, and the subsequent ReloadLoopIncrementalAsync only decodes (EnsureCachedAsync then returns
		// the already-cached file instantly). Bounded parallelism like the backfill; already-cached keys
		// are a cheap File.Exists no-op inside EnsureCachedAsync, and every failure is per-frame + non-fatal
		// (the fold-in will just download that one itself, as before).
		private async Task PrefetchArchiveFramesAsync(RadarSite site, IReadOnlyList<string> newKeys, CancellationToken ct)
		{
			var loaded = new HashSet<string>(_loadedKeys, StringComparer.Ordinal);
			var toPrefetch = newKeys.Where(k => !loaded.Contains(k)).ToList();
			if (toPrefetch.Count == 0)
			{
				return;
			}

			using var gate = new SemaphoreSlim(MaxParallelBackfill);
			var tasks = toPrefetch.Select(async key =>
			{
				try
				{
					await gate.WaitAsync(ct);
				}
				catch (OperationCanceledException)
				{
					return;
				}
				try
				{
					await _radarService.EnsureCachedAsync(site, key, ct);
				}
				catch (OperationCanceledException)
				{
					// Selection changed; stop.
				}
				catch (Exception ex)
				{
					Services.RadarDiagnostics.Log("vm", "prefetch.fail", ("key", key), ("error", ex.Message));
				}
				finally
				{
					gate.Release();
				}
			});
			await Task.WhenAll(tasks);
			Services.RadarDiagnostics.Log("vm", "prefetch", ("count", toPrefetch.Count), ("newest", newKeys[^1]));
		}

		// Pulls the freshest chunks-bucket frame and (when newer) updates the trailing live slot
		// in place — or appends one if the loop didn't have a live frame yet — keeping the newest
		// frame ~1-2 min old between the slower (~5 min) archive reloads. Polls fast until the
		// first live frame lands (LiveFrameRetrySeconds), then settles to LiveFrameRefreshSeconds.
		private async Task RunLiveFrameRefreshAsync(RadarSite site, CancellationToken ct)
		{
			try
			{
				while (true)
				{
					var interval = _hasLiveFrame ? RefreshIntervalSeconds : LiveFrameRetrySeconds;
					// Schedule relative to the LAST poll, whoever ran it — an archive reload runs its
					// own inline live poll (LoadLoopCoreAsync → RefreshLiveFrameAsync), so anchoring on
					// _lastLivePollAt pushes this timer out instead of double-fetching ~3s later.
					var sinceLast = _lastLivePollAt is { } last ? (DateTimeOffset.Now - last).TotalSeconds : interval;
					var wait = Math.Max(1.0, interval - sinceLast);
					_livePollCycleStart = DateTimeOffset.Now;
					_nextLivePollAt = _livePollCycleStart.Value.AddSeconds(wait);
					OnPropertyChanged(nameof(RadarNextFrameProgress)); // reset the bar at the cycle start
					// The on-map sweep is no longer a continuous phase-locked rotation — it pulses once
					// when a genuinely-new frame actually lands (see ApplyLiveFrameAsync), so nothing to
					// start here.
					await Task.Delay(TimeSpan.FromSeconds(wait), ct);

					if (!ReferenceEquals(_selectedRadarOption?.Site, site))
					{
						return;
					}

					// If a poll snuck in during our wait (e.g. a reload's inline poll), don't double
					// up — loop to recompute the next deadline from that poll instead.
					if (_lastLivePollAt is { } recent && (DateTimeOffset.Now - recent).TotalSeconds < interval - 1)
					{
						continue;
					}

					// Gate against a concurrent archive (re)load mutating the same frame state.
					await _loopGate.WaitAsync(ct);
					try
					{
						if (ReferenceEquals(_selectedRadarOption?.Site, site))
						{
							await RefreshLiveFrameAsync(site, ct);
						}
					}
					finally
					{
						_loopGate.Release();
					}
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed or app shutting down.
			}
		}
	}
}
