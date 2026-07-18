using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the SPC watch-box subsystem — active Tornado / Severe Thunderstorm Watches. These
	/// are current-conditions alerts (not a forecast), so they live in their OWN subsystem VM (surfaced
	/// under NowCast in the UI) rather than on <see cref="OutlookViewModel"/>. Owns the show/hide toggle,
	/// the ~2-min background refresh loop, and the map pushes. Fetch/cache is in
	/// <see cref="ISpcWatchService"/>; the map is driven through <see cref="IMapService"/>.
	/// </summary>
	public sealed class WatchesViewModel : INotifyPropertyChanged
	{
		private readonly IMapService _mapService;
		private readonly ISpcWatchService _watchService;
		private readonly DispatcherQueue _dispatcher;

		// Readiness guard: watch commands only run once the map page has reported 'mapReady'
		// (set by OnMapsReadyAsync, called from MapViewModel.OnMapsReadyAsync).
		private bool _isMapReady;

		// Watch-box overlay toggle. Default OFF so the app launches with no watch boxes drawn.
		private bool _showWatches;

		// Overall opacity of the watch polygons (fill + outline together). Default 1.0 = the current look.
		private double _watchesOpacity = 1.0;

		public WatchesViewModel(IMapService mapService, ISpcWatchService watchService)
		{
			_mapService = mapService;
			_watchService = watchService;
			_dispatcher = DispatcherQueue.GetForCurrentThread();
		}

		/// <summary>Show the SPC watch boxes — Tornado / Severe Thunderstorm Watches — on the map
		/// (NowCast card toggle, default off).</summary>
		public bool ShowWatches
		{
			get => _showWatches;
			set
			{
				if (_showWatches == value)
				{
					return;
				}

				_showWatches = value;
				OnPropertyChanged();
				if (_isMapReady)
				{
					_ = _mapService.SetWatchesVisibleAsync(value);
				}
			}
		}

		/// <summary>Overall opacity (0-1) of the watch polygons — scales the faint fill and the bold outline
		/// together (1 = the default look). NowCast card slider; independent of the show/hide toggle.</summary>
		public double WatchesOpacity
		{
			get => _watchesOpacity;
			set
			{
				if (_watchesOpacity == value)
				{
					return;
				}

				_watchesOpacity = value;
				OnPropertyChanged();
				if (_isMapReady)
				{
					_ = _mapService.SetWatchesOpacityAsync(value);
				}
			}
		}

		/// <summary>Kicks off the watch background refresh loop (called once at launch).</summary>
		public void StartBackgroundRefresh() => _ = RefreshWatchesInBackgroundAsync();

		// SPC watches change on roughly hourly scales but expire continuously; a few-minute refresh
		// keeps the active set current (the service re-filters to in-effect watches each cycle).
		private static readonly TimeSpan WatchRefreshInterval = TimeSpan.FromMinutes(2);

		private Task RefreshWatchesInBackgroundAsync() => BackgroundRefresh.RunPeriodicAsync(WatchRefreshInterval, async first =>
		{
			try
			{
				var result = await _watchService.RefreshAsync();
				System.Diagnostics.Debug.WriteLine($"[SPC] watches refresh: {result.Status} active={result.ActiveCount} {result.Message}");

				// Re-point the page at the cache so it reloads — on launch (first-run empty cache) and
				// whenever a cycle pulled fresh data.
				if (first || result.Status is SpcWatchFetchStatus.Updated)
				{
					_dispatcher.TryEnqueue(OnWatchesRefreshed);
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[SPC] watches refresh aborted: {ex.Message}");
			}
		});

		/// <summary>Called by MapViewModel once the map page is ready: points the page at the cached watch
		/// boxes and applies the current toggle state.</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			await _mapService.SetWatchSourceAsync(_watchService.WatchesUrl);
			await _mapService.SetWatchesOpacityAsync(_watchesOpacity);
			await _mapService.SetWatchesVisibleAsync(_showWatches);
		}

		/// <summary>
		/// Re-pushes the watch source URL to the page so it re-fetches the freshly-cached boxes. Called
		/// after a background watch refresh; the page only re-fetches when the watch layer is shown.
		/// </summary>
		public void OnWatchesRefreshed()
		{
			if (_isMapReady)
			{
				_ = _mapService.SetWatchSourceAsync(_watchService.WatchesUrl);
			}
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
