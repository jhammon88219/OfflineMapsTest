using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>Which temporal feature's settings card is floating above the overlay bar (opened by a
	/// split-toggle's cog). At most one is open at a time; <see cref="None"/> = no card showing.</summary>
	public enum TemporalCard { None, Past, Now, Fore }

	/// <summary>
	/// View model for the NON-radar map concerns: selectable basemap styles + current selection,
	/// the SPC outlook day/product selection + overlay opacity, SPC watches, the outlook info/times,
	/// and map markers + user location. The radar subsystem lives in <see cref="Radar"/>
	/// (<see cref="RadarViewModel"/>). Drives the map through <see cref="IMapService"/>.
	/// </summary>
	public sealed class MapViewModel : ObservableObject
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


		public MapViewModel(IMapService mapService, IStyleProvider styleProvider, IRegionProvider regionProvider, ISpcOutlookService spcOutlookService, ISpcWatchService watchService, IWarningService warningService, IStormReportService stormReportService, IRadarSiteProvider radarSiteProvider, ILevel2RadarService radarService, ILocationService locationService, IDowEventProvider dowEventProvider, IDispatcher dispatcher)
		{
			_mapService = mapService;
			_styleProvider = styleProvider;
			_regionProvider = regionProvider;

			// Each subsystem lives in its own view model (progressively split out of this class);
			// the transport-bar section controls bind slices of them.
			Radar = new RadarViewModel(mapService, radarSiteProvider, radarService, dowEventProvider);
			Outlook = new OutlookViewModel(mapService, spcOutlookService, dispatcher);
			PastOutlook = new PastOutlookViewModel(mapService, spcOutlookService, Radar);
			Watches = new WatchesViewModel(mapService, watchService, dispatcher);
			Warnings = new WarningsViewModel(mapService, warningService, dispatcher);
			StormReports = new StormReportsViewModel(mapService, stormReportService, Radar, dispatcher);
			Markers = new MarkersViewModel(mapService, locationService);
			SiteExplorer = new RadarSiteExplorerViewModel(Radar, Markers, radarService, mapService);

			// The Past/Now/Fore toggles PROJECT subsystem state (see the Temporal toggles region), so keep
			// them honest: re-raise them — and close a now-inactive feature's settings card — whenever the
			// radar mode/loop or the outlook overlay changes, including changes NOT driven by the toggles
			// (e.g. clicking an on-map radar site marker starts a live loop, which should light NowCast).
			Radar.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(RadarViewModel.IsPastEventMode))
				{
					OnPropertyChanged(nameof(IsPastCast));
					// Entering replay takes the radar layer — disarm the (mutually exclusive) live toggle.
					if (Radar.IsPastEventMode && _isNowCast) { _isNowCast = false; OnPropertyChanged(nameof(IsNowCast)); }
					// The map has ONE outlook layer: hand it to the historical (PastOutlook) overlay in past
					// mode and back to the live outlook otherwise. Entering clears the live outlook (showing
					// today's forecast over historical radar would be wrong); PastOutlook then drives it.
					if (Radar.IsPastEventMode && Outlook.IsOutlookVisible) { Outlook.IsOutlookVisible = false; }
					PastOutlook.OnPastModeChanged(Radar.IsPastEventMode);
					CloseCardIfInactive();
				}
				else if (e.PropertyName == nameof(RadarViewModel.HasRadarLoop))
				{
					// A live loop starting (e.g. an on-map site-marker click) arms NowCast so it reflects reality.
					if (Radar.HasRadarLoop && !Radar.IsPastEventMode && !_isNowCast)
					{
						_isNowCast = true;
						OnPropertyChanged(nameof(IsNowCast));
						CloseCardIfInactive();
					}
				}
			};
			Outlook.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(OutlookViewModel.IsOutlookVisible))
				{
					OnPropertyChanged(nameof(IsForeCast));
					CloseCardIfInactive();
				}
			};

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

		/// <summary>The historical SPC outlook overlay for PastCast (day 1-3 product for the replay date,
		/// from the IEM archive). Shares the map's single outlook layer with <see cref="Outlook"/>; this VM
		/// owns it in past mode (see the IsPastEventMode handler above).</summary>
		public PastOutlookViewModel PastOutlook { get; }

		/// <summary>The SPC watch-box subsystem view model (Tornado / Severe Thunderstorm Watches —
		/// current-conditions alerts surfaced under NowCast, with their own toggle + refresh loop).</summary>
		public WatchesViewModel Watches { get; }

		/// <summary>The storm-based warning subsystem view model (active Tornado / Severe Thunderstorm
		/// Warnings — the modern forecaster-drawn polygons — surfaced under NowCast with their own toggle
		/// + faster refresh loop; sits above the watch boxes on the map).</summary>
		public WarningsViewModel Warnings { get; }

		/// <summary>The SPC storm-reports verification overlay VM (Tornado / Wind / Hail dots for the active
		/// convective day — the replay day in PastCast, today in NowCast — with per-type toggles). Its own
		/// map overlay (top of the stack); surfaced in both the Past and Now cards.</summary>
		public StormReportsViewModel StormReports { get; }

		/// <summary>The map markers + user-location subsystem view model (locate action + marker editor).</summary>
		public MarkersViewModel Markers { get; }

		/// <summary>The Radar Site Explorer subsystem view model (searchable/filterable browser over the
		/// whole radar network + per-site detail). Opened by the "Sites" button on the bar.</summary>
		public RadarSiteExplorerViewModel SiteExplorer { get; }

		// ===== Temporal toggles (Past / Now / Fore) — INDEPENDENT, deselectable ==========================
		// The three toggles are independent on/off switches, each a PROJECTION of its subsystem's real
		// state (no duplicated flag that could desync): IsPastCast ↔ Radar replay mode, IsNowCast ↔ a live
		// radar loop being shown, IsForeCast ↔ the SPC outlook overlay. Setting a toggle drives its
		// subsystem; the Radar/Outlook PropertyChanged subscriptions (in the ctor) re-raise the toggles
		// when that state changes from anywhere (e.g. clicking an on-map site marker lights NowCast).
		// Past and Now are mutually exclusive because the radar layer is EITHER live OR replaying — turning
		// one on takes the layer and clears the other; Fore's outlook overlay is independent and stacks on
		// either. With ALL THREE OFF the map is a blank basemap (the "cleared" state — click the active
		// toggle to reach it). NowCast is the launch default (armed, no site loaded). OpenCard tracks which feature's settings card floats above the bar (one at a
		// time); a feature turning off closes its card (CloseCardIfInactive).
		private TemporalCard _openCard = TemporalCard.None;

		/// <summary>PastCast (historical replay). Projection of <see cref="RadarViewModel.IsPastEventMode"/>:
		/// on enters replay (clearing any live loop), off exits replay to a blank basemap. Turning it on
		/// takes the radar layer from NowCast. Re-raised via the Radar subscription.</summary>
		public bool IsPastCast
		{
			get => Radar.IsPastEventMode;
			set
			{
				if (value == Radar.IsPastEventMode) { return; }
				Radar.IsPastEventMode = value; // both directions clear the layer (enter = arm replay, leave = blank)
			}
		}

		// NowCast is an ARMED toggle (a stored flag), NOT a pure projection: "live radar mode is on." Unlike
		// PastCast (Radar.IsPastEventMode) and ForeCast (Outlook.IsOutlookVisible) — both genuine persistent
		// states — "live mode" has no subsystem flag (it's just "not replay", the default), so a projection
		// off loop-existence would snap back + DISABLE the cog whenever no site is loaded. Storing it lets
		// the toggle/cog stay on with a blank-but-armed radar. Default ON (launch armed for live radar, so a
		// site-marker click just works); a live loop starting (marker click) arms it, entering replay disarms
		// it — see the Radar subscription. Set as a field initializer, NOT via the setter, so arming at
		// construction issues no map command (there is no loop to clear and the map isn't ready yet).
		private bool _isNowCast = true;

		/// <summary>NowCast (live radar). On = live mode armed (leaves replay; a site is then picked by
		/// clicking its on-map marker); off = clears the live loop to a blank basemap. Mutually exclusive
		/// with PastCast (the radar layer is live OR replaying).</summary>
		public bool IsNowCast
		{
			get => _isNowCast;
			set
			{
				if (!SetProperty(ref _isNowCast, value)) { return; }
				if (value)
				{
					if (Radar.IsPastEventMode) { Radar.IsPastEventMode = false; } // leave replay for live mode
				}
				else if (!Radar.IsPastEventMode)
				{
					Radar.SelectedRadarOption = Radar.RadarOptions[0]; // "None" → clear the live loop to a blank basemap
				}
				CloseCardIfInactive();
			}
		}

		/// <summary>ForeCast (SPC outlook overlay). Independent projection of
		/// <see cref="OutlookViewModel.IsOutlookVisible"/> — stacks on live or replay radar. Re-raised via
		/// the Outlook subscription.</summary>
		public bool IsForeCast
		{
			get => Outlook.IsOutlookVisible;
			set
			{
				if (value == Outlook.IsOutlookVisible) { return; }
				Outlook.IsOutlookVisible = value;
			}
		}

		// Closes an open settings card whose feature just turned off (its cog is now disabled). Called from
		// the toggle setters' subsystem subscriptions so a card can't linger over an inactive feature.
		private void CloseCardIfInactive()
		{
			if ((_openCard == TemporalCard.Past && !IsPastCast)
				|| (_openCard == TemporalCard.Now && !IsNowCast)
				|| (_openCard == TemporalCard.Fore && !IsForeCast))
			{
				OpenCard = TemporalCard.None;
			}
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
				if (SetProperty(ref _openCard, value))
				{
					OnPropertyChanged(nameof(IsPastCardOpen));
					OnPropertyChanged(nameof(IsNowCardOpen));
					OnPropertyChanged(nameof(IsForeCardOpen));
				}
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

		// ===== App settings card (right side of the bar) =================================================
		// The mirror image of the temporal cards: opened by a settings COG on the RIGHT edge of the bar and
		// hidden by the card's own down-triangle. Deliberately INDEPENDENT of TemporalMode/OpenCard — it's
		// app-wide, not tied to a time-frame, so switching temporal modes never closes it and it isn't part
		// of the one-card-at-a-time temporal group.
		private bool _isSettingsCardOpen;

		/// <summary>Whether the app-wide settings card floats above the bar (right-aligned). Two-way: the
		/// settings cog toggles it; the card's down-triangle clears it.</summary>
		public bool IsSettingsCardOpen
		{
			get => _isSettingsCardOpen;
			set => SetProperty(ref _isSettingsCardOpen, value);
		}

		// The Radar Site Explorer's open state — app-wide like IsSettingsCardOpen and deliberately
		// independent of the temporal cards, so it can be open alongside any of them.
		private bool _isSiteExplorerOpen;

		/// <summary>Whether the Radar Site Explorer panel is showing (toggled by the "Sites" button).</summary>
		public bool IsSiteExplorerOpen
		{
			get => _isSiteExplorerOpen;
			set => SetProperty(ref _isSiteExplorerOpen, value);
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
			await Warnings.OnMapsReadyAsync();
			await Radar.OnMapsReadyAsync();
			await PastOutlook.OnMapsReadyAsync();
			await StormReports.OnMapsReadyAsync();
		}
	}
}
