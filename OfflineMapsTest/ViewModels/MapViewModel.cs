using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OfflineMapsTest.Models;
using OfflineMapsTest.Services;

namespace OfflineMapsTest.ViewModels
{
	/// <summary>Which temporal time-frame the map is showing. Exactly one is active at a time
	/// (a 3-way radio): <see cref="Now"/> = normal live radar (the default), <see cref="Past"/> =
	/// historical replay, <see cref="Fore"/> = SPC outlooks. Past and Fore are mutually exclusive.</summary>
	public enum TemporalMode { Now, Past, Fore }

	/// <summary>Which temporal feature's settings card is floating above the overlay bar (opened by a
	/// split-toggle's cog). At most one is open at a time; <see cref="None"/> = no card showing.</summary>
	public enum TemporalCard { None, Past, Now, Fore }

	/// <summary>
	/// View model for the NON-radar map concerns: selectable basemap styles + current selection,
	/// the SPC outlook day/product selection + overlay opacity, SPC watches, the outlook info/times,
	/// and map markers + user location. The radar subsystem lives in <see cref="Radar"/>
	/// (<see cref="RadarViewModel"/>). Drives the map through <see cref="IMapService"/>.
	/// </summary>
	public sealed class MapViewModel : INotifyPropertyChanged
	{
		private readonly IMapService _mapService;
		private readonly IStyleProvider _styleProvider;
		private readonly IRegionProvider _regionProvider;

		// Readiness guard: the map page must have reported 'mapReady' before style /
		// outlook commands can succeed. The view calls OnMapsReadyAsync() once the map
		// loads; until then a style change is stored and the overlay is deferred.
		private bool _isMapReady;

		private MapStyle? _selectedStyle;

		// The region the main map is framed on (CONUS).
		private MapRegion? _mainRegion;


		public MapViewModel(IMapService mapService, IStyleProvider styleProvider, IRegionProvider regionProvider, ISpcOutlookService spcOutlookService, ISpcWatchService watchService, IRadarSiteProvider radarSiteProvider, ILevel2RadarService radarService, ILocationService locationService, IDowEventProvider dowEventProvider)
		{
			_mapService = mapService;
			_styleProvider = styleProvider;
			_regionProvider = regionProvider;

			// Each subsystem lives in its own view model (progressively split out of this class);
			// the transport-bar section controls bind slices of them.
			Radar = new RadarViewModel(mapService, radarSiteProvider, radarService, dowEventProvider);
			Outlook = new OutlookViewModel(mapService, spcOutlookService);
			Watches = new WatchesViewModel(mapService, watchService);
			Markers = new MarkersViewModel(mapService, locationService);

			AvailableStyles = _styleProvider.GetStyles();

			// Assign the backing field directly (not the setter) so the default
			// selection does NOT trigger a map command during construction. The page
			// loads this style via its URL, so there is nothing to re-apply. Default to
			// Data Viz Black.
			_selectedStyle = AvailableStyles.FirstOrDefault(s => s.Id == "dataVizBlack")
				?? AvailableStyles.FirstOrDefault();

			// The main map is framed on CONUS.
			var regions = _regionProvider.GetRegions();
			_mainRegion = regions.FirstOrDefault(r => r.Id == "conus") ?? regions.FirstOrDefault();
		}

		/// <summary>The radar subsystem view model (sites, loop, live frame, past-event, DOW, card,
		/// color scale, inspector). The transport-bar section controls bind to this.</summary>
		public RadarViewModel Radar { get; }

		/// <summary>The SPC outlook subsystem view model (day/product selection, info card,
		/// next-update progress, background refresh).</summary>
		public OutlookViewModel Outlook { get; }

		/// <summary>The SPC watch-box subsystem view model (Tornado / Severe Thunderstorm Watches —
		/// current-conditions alerts surfaced under NowCast, with their own toggle + refresh loop).</summary>
		public WatchesViewModel Watches { get; }

		/// <summary>The map markers + user-location subsystem view model (locate action + marker editor).</summary>
		public MarkersViewModel Markers { get; }

		// ===== Temporal-mode coordination (the Past / Now / Fore split-toggle buttons) ==================
		// The three temporal toggles are a 3-way radio: exactly one mode is active. Routing them through a
		// single TemporalMode enum is what enforces "Past and Fore can't both be on" and lets NowCast clear
		// both. The bool facets (IsPastCast/…) are what the toggles' IsChecked bind to; the mode setter
		// drives the actual subsystem flags (Radar.IsPastEventMode / Outlook.IsOutlookVisible) so those
		// stay in lock-step. OpenCard tracks which feature's settings card floats above the bar (one at a
		// time); changing mode dismisses any open card since its feature may no longer be active.
		private TemporalMode _temporalMode = TemporalMode.Now;
		private TemporalCard _openCard = TemporalCard.None;

		/// <summary>The active temporal time-frame. Setting it drives the subsystem flags and closes any
		/// open settings card. Defaults to <see cref="TemporalMode.Now"/> (live radar), matching the app's
		/// launch state (no replay, no outlook) — assigned as a field so construction pushes no commands.</summary>
		public TemporalMode TemporalMode
		{
			get => _temporalMode;
			set
			{
				if (_temporalMode == value)
				{
					return;
				}

				_temporalMode = value;

				// Drive the subsystems. These setters are guarded (no-op when unchanged), so only the
				// genuinely-changing one does work — e.g. Past→Fore turns replay off and the outlook on.
				Radar.IsPastEventMode = value == TemporalMode.Past;
				Outlook.IsOutlookVisible = value == TemporalMode.Fore;

				// A mode change dismisses any open card (its feature/cog may now be disabled).
				OpenCard = TemporalCard.None;

				OnPropertyChanged();
				OnPropertyChanged(nameof(IsPastCast));
				OnPropertyChanged(nameof(IsNowCast));
				OnPropertyChanged(nameof(IsForeCast));
			}
		}

		/// <summary>PastCast toggle state (feature = historical replay). Setting it true selects
		/// <see cref="TemporalMode.Past"/>; a ToggleButton trying to un-check the active mode is bounced
		/// back (radio semantics — one mode is always active).</summary>
		public bool IsPastCast
		{
			get => _temporalMode == TemporalMode.Past;
			set { if (value) { TemporalMode = TemporalMode.Past; } else if (_temporalMode == TemporalMode.Past) { OnPropertyChanged(); } }
		}

		/// <summary>NowCast toggle state (feature = live radar). Setting it true clears Past/Fore.</summary>
		public bool IsNowCast
		{
			get => _temporalMode == TemporalMode.Now;
			set { if (value) { TemporalMode = TemporalMode.Now; } else if (_temporalMode == TemporalMode.Now) { OnPropertyChanged(); } }
		}

		/// <summary>ForeCast toggle state (feature = SPC outlooks). Setting it true selects
		/// <see cref="TemporalMode.Fore"/>.</summary>
		public bool IsForeCast
		{
			get => _temporalMode == TemporalMode.Fore;
			set { if (value) { TemporalMode = TemporalMode.Fore; } else if (_temporalMode == TemporalMode.Fore) { OnPropertyChanged(); } }
		}

		/// <summary>Which feature's settings card is showing above the bar (at most one). Opened by a
		/// split-toggle's cog, hidden by the card's own down-triangle. Independent of whether the feature is
		/// on for reading, but a cog can only set it while its feature is active (cogs are disabled otherwise),
		/// and a mode change resets it to <see cref="TemporalCard.None"/>.</summary>
		public TemporalCard OpenCard
		{
			get => _openCard;
			set
			{
				if (_openCard == value)
				{
					return;
				}

				_openCard = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(IsPastCardOpen));
				OnPropertyChanged(nameof(IsNowCardOpen));
				OnPropertyChanged(nameof(IsForeCardOpen));
			}
		}

		/// <summary>PastCast settings-card visibility (two-way: the cog toggles it; the card's triangle
		/// clears it). Setting false only closes it when it was the one open.</summary>
		public bool IsPastCardOpen
		{
			get => _openCard == TemporalCard.Past;
			set { if (value) { OpenCard = TemporalCard.Past; } else if (_openCard == TemporalCard.Past) { OpenCard = TemporalCard.None; } }
		}

		/// <summary>NowCast settings-card visibility (placeholder card for now).</summary>
		public bool IsNowCardOpen
		{
			get => _openCard == TemporalCard.Now;
			set { if (value) { OpenCard = TemporalCard.Now; } else if (_openCard == TemporalCard.Now) { OpenCard = TemporalCard.None; } }
		}

		/// <summary>ForeCast settings-card visibility.</summary>
		public bool IsForeCardOpen
		{
			get => _openCard == TemporalCard.Fore;
			set { if (value) { OpenCard = TemporalCard.Fore; } else if (_openCard == TemporalCard.Fore) { OpenCard = TemporalCard.None; } }
		}

		public IReadOnlyList<MapStyle> AvailableStyles { get; }

		/// <summary>The region the main, full-window map is framed on.</summary>
		public MapRegion? MainRegion => _mainRegion;

		// NOTE: the old left/right tool-window docks (and the abandoned drag-dock direction) are gone;
		// the UI is now the bottom OverlayBar (Controls/OverlayBar) + section controls. The bar's
		// show/hide state is pure view state on the control, not here.



		public MapStyle? SelectedStyle
		{
			get => _selectedStyle;
			set
			{
				if (ReferenceEquals(_selectedStyle, value))
				{
					return;
				}

				_selectedStyle = value;
				OnPropertyChanged();

				// Only push a style change once the map can receive it. Pre-ready
				// selections are stored and applied later by OnMapsReadyAsync.
				if (_isMapReady && value is not null)
				{
					_ = _mapService.ApplyStyleAsync(value);
				}
			}
		}



		/// <summary>
		/// Called by the view once the map page has fired its 'load' event. Enables live
		/// style switching and shows the initially-selected outlook.
		/// </summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;

			// The page loaded the selected style via its URL; re-apply it to pick up any
			// change made before the map was ready (idempotent when unchanged).
			if (_selectedStyle is not null)
			{
				await _mapService.ApplyStyleAsync(_selectedStyle);
			}

			// Hand off subsystem startup: outlook (startup overlay + progress), watches (source + toggle),
			// and radar (site markers, offline-status loop, radar progress bar). Markers has no startup
			// work — just flip its readiness so user-driven locate/marker pushes are allowed.
			Markers.SetMapReady();
			await Outlook.OnMapsReadyAsync();
			await Watches.OnMapsReadyAsync();
			await Radar.OnMapsReadyAsync();
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}
}
