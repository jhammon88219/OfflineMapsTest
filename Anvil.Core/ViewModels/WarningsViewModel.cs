using System;
using System.Threading.Tasks;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the storm-based WARNING subsystem — active Tornado / Severe Thunderstorm Warnings
	/// (the modern forecaster-drawn polygons). Sibling of <see cref="WatchesViewModel"/>: watches are the
	/// large outlook areas, warnings are the imminent-threat polygons, so each gets its own layer, toggle,
	/// and refresh loop. Surfaced under NowCast in the UI (current-conditions alerts, not a forecast).
	/// Owns the show/hide toggle, the ~1-min background refresh loop, and the map pushes. Fetch/cache is in
	/// <see cref="IWarningService"/>; the map is driven through <see cref="IMapService"/>.
	/// </summary>
	public sealed class WarningsViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly IWarningService _warningService;
		private readonly IDispatcher _dispatcher;

		// Readiness guard: warning commands only run once the map page has reported 'mapReady'
		// (set by OnMapsReadyAsync, called from MapViewModel.OnMapsReadyAsync).
		private bool _isMapReady;

		// Warning-polygon overlay toggle. Default OFF so the app launches with no warnings drawn.
		private bool _showWarnings;

		// Overall opacity of the warning polygons (fill + outline together). Default 1.0 = the current look.
		private double _warningsOpacity = 1.0;

		public WarningsViewModel(IMapService mapService, IWarningService warningService, IDispatcher dispatcher)
		{
			_mapService = mapService;
			_warningService = warningService;
			_dispatcher = dispatcher;
		}

		/// <summary>Show the storm-based warning polygons — active Tornado / Severe Thunderstorm
		/// Warnings — on the map (NowCast card toggle, default off).</summary>
		public bool ShowWarnings
		{
			get => _showWarnings;
			set
			{
				if (SetProperty(ref _showWarnings, value) && _isMapReady)
				{
					_ = _mapService.SetWarningsVisibleAsync(value);
				}
			}
		}

		/// <summary>Overall opacity (0-1) of the warning polygons — scales the faint fill and the bold
		/// outline together (1 = the default look). NowCast card slider; independent of the show/hide toggle.</summary>
		public double WarningsOpacity
		{
			get => _warningsOpacity;
			set
			{
				if (SetProperty(ref _warningsOpacity, value) && _isMapReady)
				{
					_ = _mapService.SetWarningsOpacityAsync(value);
				}
			}
		}

		// Live per-type active-warning counts for the NowCast readout, updated each refresh (UI thread).
		private int _tornadoWarningCount;
		private int _severeWarningCount;

		/// <summary>Number of active Tornado Warnings (NowCast readout). Updated each refresh cycle.</summary>
		public int TornadoWarningCount
		{
			get => _tornadoWarningCount;
			private set => SetProperty(ref _tornadoWarningCount, value);
		}

		/// <summary>Number of active Severe Thunderstorm Warnings (NowCast readout). Updated each cycle.</summary>
		public int SevereWarningCount
		{
			get => _severeWarningCount;
			private set => SetProperty(ref _severeWarningCount, value);
		}

		/// <summary>Kicks off the warning background refresh loop (called once at launch).</summary>
		public void StartBackgroundRefresh() => _ = RefreshWarningsInBackgroundAsync();

		// Warnings are short-fused (issued/expiring on ~30-45 min cycles, new ones appearing continuously).
		// The endpoint sends Cache-Control: max-age=0 (revalidated per request, no edge-TTL floor), so
		// polling faster genuinely gets fresher data. We poll ADAPTIVELY: fast while warnings are active
		// (rapid updates as an event unfolds — new warnings, polygon changes), and slower when quiet. The
		// quiet interval also bounds how late the FIRST new warning can appear, so it isn't set too high.
		// A ~19 KB GeoJSON per cycle makes even the fast rate cheap; tune here.
		private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(15); // warnings on screen
		private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);   // none active

		// Best-known "are there active warnings right now" signal, updated only on a definitive fetch
		// (a cycle that actually pulled a fresh feature count). Failures/kept-last-known-good leave it
		// unchanged, so a transient hiccup doesn't drop us back to the slow cadence while a storm is on.
		private bool _hasActiveWarnings;

		private Task RefreshWarningsInBackgroundAsync() => BackgroundRefresh.RunAdaptiveAsync(async first =>
		{
			try
			{
				var result = await _warningService.RefreshAsync();
				System.Diagnostics.Debug.WriteLine($"[NWS] warnings refresh: {result.Status} active={result.ActiveCount} {result.Message}");

				// A completed fetch tells us the current active state; a failure leaves the prior state.
				if (result.Status is WarningFetchStatus.Updated)
				{
					_hasActiveWarnings = result.ActiveCount > 0;

					// Push the per-type counts to the NowCast readout on the UI thread, then reload the map.
					_dispatcher.Post(() =>
					{
						TornadoWarningCount = result.TornadoCount;
						SevereWarningCount = result.SevereCount;
						OnWarningsRefreshed();
					});
				}
				else if (first)
				{
					// First cycle with no data yet — still point the page at the (empty) cache.
					_dispatcher.Post(OnWarningsRefreshed);
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[NWS] warnings refresh aborted: {ex.Message}");
			}

			// Poll fast while anything is active, slow when the map is clear.
			return _hasActiveWarnings ? ActiveInterval : IdleInterval;
		});

		/// <summary>Called by MapViewModel once the map page is ready: points the page at the cached
		/// warning polygons and applies the current toggle state.</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			await _mapService.SetWarningSourceAsync(_warningService.WarningsUrl);
			await _mapService.SetWarningsOpacityAsync(_warningsOpacity);
			await _mapService.SetWarningsVisibleAsync(_showWarnings);
		}

		/// <summary>
		/// Re-pushes the warning source URL to the page so it re-fetches the freshly-cached polygons.
		/// Called after a background warning refresh; the page only re-fetches when the layer is shown.
		/// </summary>
		public void OnWarningsRefreshed()
		{
			if (_isMapReady)
			{
				_ = _mapService.SetWarningSourceAsync(_warningService.WarningsUrl);
			}
		}
	}
}
