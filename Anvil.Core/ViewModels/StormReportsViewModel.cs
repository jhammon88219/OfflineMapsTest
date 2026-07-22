using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the SPC storm-reports verification overlay — the filtered Tornado / Wind / Hail
	/// reports SPC uses to verify its outlooks, drawn as colored dots so you can pull up an outlook and see
	/// how it verified. Works in BOTH temporal modes off one map overlay: in PastCast it shows the replay
	/// day's reports (keyed to <see cref="RadarViewModel.ReplayStartUtc"/>, immutable), in NowCast today's
	/// (re-fetched on a background loop, since the day's reports accumulate). Per-type toggles filter which
	/// dots show; the reports are always keyed to the SPC convective day (12Z→12Z), so they line up with the
	/// outlook that was valid over that window. Fetch/cache is in <see cref="IStormReportService"/>; the map
	/// is driven through <see cref="IMapService"/>.
	/// </summary>
	public sealed class StormReportsViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly IStormReportService _reportService;
		private readonly RadarViewModel _radar;
		private readonly IDispatcher _dispatcher;

		private bool _isMapReady;
		private int _applyToken;            // guards a stale async apply when the selection/day changes mid-fetch
		private DateOnly? _loadedDay;       // the convective day whose points are currently on the map (null = none)

		public StormReportsViewModel(IMapService mapService, IStormReportService reportService, RadarViewModel radar, IDispatcher dispatcher)
		{
			_mapService = mapService;
			_reportService = reportService;
			_radar = radar;
			_dispatcher = dispatcher;

			// Re-key the overlay to the new convective day when the temporal mode flips or the replay date
			// changes (only matters while some type is shown; the toggle setters cover the show/hide case).
			_radar.PropertyChanged += OnRadarChanged;
		}

		// ── Per-type toggles (default all off, so the app launches with no dots) ──

		private bool _showTornado;
		public bool ShowTornado
		{
			get => _showTornado;
			set { if (SetProperty(ref _showTornado, value)) { OnKindToggled(); } }
		}

		private bool _showWind;
		public bool ShowWind
		{
			get => _showWind;
			set { if (SetProperty(ref _showWind, value)) { OnKindToggled(); } }
		}

		private bool _showHail;
		public bool ShowHail
		{
			get => _showHail;
			set { if (SetProperty(ref _showHail, value)) { OnKindToggled(); } }
		}

		private bool AnyShown => _showTornado || _showWind || _showHail;

		// ── Opacity ──

		private double _opacity = 0.9;
		public double Opacity
		{
			get => _opacity;
			set
			{
				if (SetProperty(ref _opacity, value) && _isMapReady)
				{
					_ = _mapService.SetStormReportsOpacityAsync(value);
				}
			}
		}

		// ── Readouts (per-type counts for the card, like the warning "Active" row) ──

		private int _tornadoCount, _windCount, _hailCount;
		public int TornadoCount { get => _tornadoCount; private set => SetProperty(ref _tornadoCount, value); }
		public int WindCount { get => _windCount; private set => SetProperty(ref _windCount, value); }
		public int HailCount { get => _hailCount; private set => SetProperty(ref _hailCount, value); }

		private string _statusText = string.Empty;
		public string StatusText
		{
			get => _statusText;
			private set => SetProperty(ref _statusText, value);
		}

		// ── Lifecycle ──

		/// <summary>Called by MapViewModel once the map page is ready.</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			await _mapService.SetStormReportsOpacityAsync(_opacity);
			if (AnyShown) { await EnsureAndShowAsync(); }
		}

		/// <summary>Kicks off the storm-report background refresh loop (called once at launch). Only does work
		/// while NowCast is showing some report type — a historical day is immutable and never refreshed.</summary>
		public void StartBackgroundRefresh() => _ = RefreshReportsInBackgroundAsync();

		// Today's reports grow through the day; a few-minute refresh keeps the NowCast overlay current.
		private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

		private Task RefreshReportsInBackgroundAsync() => BackgroundRefresh.RunPeriodicAsync(RefreshInterval, async first =>
		{
			try
			{
				// Nothing to refresh unless we're in NowCast with something shown (past days are immutable).
				if (!_isMapReady || _radar.IsPastEventMode || !AnyShown) { return; }

				var day = TodayConvectiveDay();
				var result = await _reportService.EnsureReportsAsync(day, immutable: false);
				_dispatcher.Post(() => ApplyRefreshed(day, result));
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[SPC] storm reports refresh aborted: {ex.Message}");
			}
		});

		// UI-thread continuation of a background refresh: re-point the page at the (freshly cached) file so it
		// reloads, and update the counts — but only if we're still in the state that asked for it.
		private void ApplyRefreshed(DateOnly day, StormReportResult result)
		{
			if (!_isMapReady || _radar.IsPastEventMode || !AnyShown || !result.Found) { return; }
			SetCounts(result);
			_loadedDay = day;
			_ = _mapService.SetStormReportsSourceAsync(_reportService.LocalUrl(day));
			StatusText = SummaryFor(day, result);
		}

		// ── Reactions ──

		private void OnRadarChanged(object? sender, PropertyChangedEventArgs e)
		{
			// A mode flip (past↔live) or a replay-date change moves the overlay to a different convective day.
			if (e.PropertyName is nameof(RadarViewModel.IsPastEventMode)
				or nameof(RadarViewModel.PastEventYearIndex)
				or nameof(RadarViewModel.PastEventMonthIndex)
				or nameof(RadarViewModel.PastEventDayIndex)
				or nameof(RadarViewModel.PastEventTime))
			{
				_loadedDay = null; // the day context changed — force a re-fetch on next show
				if (_isMapReady && AnyShown) { _ = EnsureAndShowAsync(); }
			}
		}

		// A type checkbox flipped: if we already have the right day loaded, just re-filter (cheap, no fetch);
		// otherwise (first enable, or the day changed) fetch + show. When nothing is shown, push the (empty)
		// filter so the dots hide without tearing the source down.
		private void OnKindToggled()
		{
			if (!_isMapReady) { return; }
			if (AnyShown && _loadedDay != ActiveDay())
			{
				_ = EnsureAndShowAsync();
			}
			else
			{
				_ = _mapService.SetStormReportKindsAsync(_showTornado, _showWind, _showHail);
			}
		}

		// ── Core ──

		// The convective day the overlay should show: the replay day in PastCast, today otherwise.
		private DateOnly ActiveDay() =>
			_radar.IsPastEventMode ? ConvectiveDay(_radar.ReplayStartUtc()) : TodayConvectiveDay();

		// Fetch (past = immutable/cache-forever, live = re-fetch) and show the active day's reports.
		private async Task EnsureAndShowAsync()
		{
			if (!_isMapReady || !AnyShown) { return; }

			var token = ++_applyToken;
			var day = ActiveDay();
			var immutable = _radar.IsPastEventMode;
			StatusText = "Loading storm reports…";

			var result = await _reportService.EnsureReportsAsync(day, immutable);
			if (token != _applyToken) { return; } // a newer day/selection won

			if (!result.Found)
			{
				StatusText = result.Error ?? "Storm reports unavailable.";
				return;
			}

			SetCounts(result);
			_loadedDay = day;
			await _mapService.SetStormReportsSourceAsync(_reportService.LocalUrl(day));
			await _mapService.SetStormReportKindsAsync(_showTornado, _showWind, _showHail);
			await _mapService.SetStormReportsOpacityAsync(_opacity);
			StatusText = SummaryFor(day, result);
		}

		private void SetCounts(StormReportResult result)
		{
			TornadoCount = result.Tornado;
			WindCount = result.Wind;
			HailCount = result.Hail;
		}

		private static string SummaryFor(DateOnly day, StormReportResult result) =>
			$"{day:MMM d, yyyy} · {result.Tornado + result.Wind + result.Hail} reports";

		// The SPC "convective day" (12Z→12Z) containing an instant — the date SPC files that day's reports
		// under (before 12Z belongs to the previous convective day). Matches the outlook's valid window.
		private static DateOnly ConvectiveDay(DateTimeOffset instant)
		{
			var d = instant.UtcDateTime;
			return DateOnly.FromDateTime(d.Hour >= 12 ? d : d.AddDays(-1));
		}

		private static DateOnly TodayConvectiveDay() => ConvectiveDay(DateTimeOffset.UtcNow);
	}
}
