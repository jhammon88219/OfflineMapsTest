using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the Radar Site Explorer — a searchable/filterable master–detail browser over the
	/// whole radar network the app can access (operational WSR-88D, research/test, TDWR). A dedicated
	/// subsystem VM constructed by the <see cref="MapViewModel"/> coordinator; it reuses existing state
	/// rather than duplicating it:
	/// <list type="bullet">
	/// <item>the list is a filtered view over <see cref="RadarViewModel.RadarSiteRows"/> (the SAME row
	/// instances whose <c>IsOffline</c> is kept current by the radar VM's status loop — one source of
	/// truth for online/offline);</item>
	/// <item>loading a site funnels into <see cref="RadarViewModel.SelectedRadarOption"/>, i.e. the exact
	/// same loop-load pipeline a map-marker click uses.</item>
	/// </list>
	/// The detail pane shows instant static facts + live status + distance, and fetches the selected
	/// site's latest-scan time and VCP/scan mode on demand via
	/// <see cref="ILevel2RadarService.GetLatestScanAsync"/>.
	/// </summary>
	public sealed class RadarSiteExplorerViewModel : INotifyPropertyChanged
	{
		private readonly RadarViewModel _radar;
		private readonly MarkersViewModel _markers;
		private readonly ILevel2RadarService _radarService;
		private readonly IMapService _mapService;

		public RadarSiteExplorerViewModel(RadarViewModel radar, MarkersViewModel markers,
			ILevel2RadarService radarService, IMapService mapService)
		{
			_radar = radar;
			_markers = markers;
			_radarService = radarService;
			_mapService = mapService;

			FilteredSites = new ObservableCollection<RadarSiteRow>();
			RebuildFiltered();

			// For the site the loop is showing, our scan read-out IS the loop's — so re-raise it whenever the
			// loop's frame time / mode / selection changes, and the two stay in lock-step (a new live frame
			// updates both at once) instead of the explorer freezing at whatever it fetched on selection.
			_radar.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName is nameof(RadarViewModel.NewestLoadedFrameTime)
					or nameof(RadarViewModel.RadarModeText)
					or nameof(RadarViewModel.SelectedRadarOption)
					or nameof(RadarViewModel.HasRadarLoop))
				{
					RaiseScan();
				}
			};
		}

		// ── Search + filters ─────────────────────────────────────────────────────────────────────
		private string _searchText = string.Empty;

		/// <summary>ICAO / name search text (case-insensitive substring). Refilters on change.</summary>
		public string SearchText
		{
			get => _searchText;
			set
			{
				if (_searchText == value) return;
				_searchText = value ?? string.Empty;
				OnPropertyChanged();
				RebuildFiltered();
			}
		}

		private int _selectedClassIndex; // 0 = All, 1 = Operational, 2 = Research, 3 = TDWR

		/// <summary>Network filter (bound to a ComboBox SelectedIndex): All / Operational / Research / TDWR.</summary>
		public int SelectedClassIndex
		{
			get => _selectedClassIndex;
			set
			{
				if (_selectedClassIndex == value) return;
				_selectedClassIndex = value;
				OnPropertyChanged();
				RebuildFiltered();
			}
		}

		private bool _onlineOnly;

		/// <summary>When set, hides sites with no recent data in the feed (offline markers).</summary>
		public bool OnlineOnly
		{
			get => _onlineOnly;
			set
			{
				if (_onlineOnly == value) return;
				_onlineOnly = value;
				OnPropertyChanged();
				RebuildFiltered();
			}
		}

		/// <summary>The filtered site rows shown in the list (shared instances from the radar VM).</summary>
		public ObservableCollection<RadarSiteRow> FilteredSites { get; }

		/// <summary>Header count, e.g. "42 of 203 sites".</summary>
		public string ResultCountText => $"{FilteredSites.Count} of {_radar.RadarSiteRows.Count} sites";

		private void RebuildFiltered()
		{
			var search = _searchText.Trim();
			IEnumerable<RadarSiteRow> q = _radar.RadarSiteRows;

			q = _selectedClassIndex switch
			{
				1 => q.Where(r => r.Site.Class == RadarSiteClass.Operational),
				2 => q.Where(r => r.Site.Class == RadarSiteClass.Research),
				3 => q.Where(r => r.Site.Class == RadarSiteClass.Tdwr),
				_ => q,
			};

			if (_onlineOnly)
			{
				q = q.Where(r => !r.IsOffline);
			}

			if (search.Length > 0)
			{
				q = q.Where(r =>
					r.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
					r.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
			}

			FilteredSites.Clear();
			foreach (var row in q)
			{
				FilteredSites.Add(row);
			}
			OnPropertyChanged(nameof(ResultCountText));
		}

		// ── Selection + detail ───────────────────────────────────────────────────────────────────
		private RadarSiteRow? _selectedSite;
		private int _detailToken; // bumped per selection so a stale async scan-info fetch is ignored

		/// <summary>The selected row driving the detail pane; setting it refreshes the detail + kicks
		/// the on-demand scan-info fetch.</summary>
		public RadarSiteRow? SelectedSite
		{
			get => _selectedSite;
			set
			{
				if (ReferenceEquals(_selectedSite, value)) return;
				_selectedSite = value;
				OnPropertyChanged();
				RaiseDetail();
				_ = LoadDetailAsync(value, ++_detailToken);
			}
		}

		/// <summary>Whether a site is selected (detail pane visibility / Load button enablement).</summary>
		public bool HasSelection => _selectedSite is not null;

		public string DetailId => _selectedSite?.Id ?? string.Empty;
		public string DetailName => _selectedSite?.Name ?? string.Empty;
		public string DetailClassLabel => _selectedSite?.ClassLabel ?? string.Empty;
		public string DetailCoords => _selectedSite?.Coords ?? string.Empty;
		public string DetailStatusText => _selectedSite is null ? string.Empty : _selectedSite.StatusLabel;

		/// <summary>Great-circle distance from the user-location marker (if any) to the selected site.</summary>
		public string DetailDistanceText
		{
			get
			{
				if (_selectedSite is null || _markers.UserLocationMarker is not { } u) return string.Empty;
				var miles = HaversineMiles(u.Latitude, u.Longitude, _selectedSite.Site.Latitude, _selectedSite.Site.Longitude);
				return $"{miles:0} mi from your location";
			}
		}

		// Scan info (latest scan time + VCP/scan mode). TWO sources, deliberately:
		//   • The site the LOOP is showing — read straight off the radar VM. It already holds the exact
		//     sweep time + mode, so the explorer and the Selected Site readout agree to the second and
		//     advance together. (Re-fetching it would mean rebuilding the live frame — a ~8-12 s,
		//     tens-of-MB chunks download per click, which would also clobber the shared live-frame cache.)
		//   • Any OTHER site — fetched on demand (GetLatestScanAsync). Nothing is displaying a time for it,
		//     so there's nothing to be out of sync with; the chunks/archive estimate is enough.
		private bool _isLoadingScanInfo;
		private RadarScanInfo? _fetchedScan;   // the on-demand result (non-loaded sites)
		private string _scanStatus = string.Empty; // "No recent data" / "Unavailable" when there's no time

		/// <summary>True while the latest-scan/VCP fetch is in flight (drives the detail spinner). Never
		/// set for the loaded site — its numbers come from the loop for free.</summary>
		public bool IsLoadingScanInfo
		{
			get => _isLoadingScanInfo;
			private set { if (_isLoadingScanInfo != value) { _isLoadingScanInfo = value; OnPropertyChanged(); } }
		}

		/// <summary>Whether the selected site is the one the loop is currently showing.</summary>
		private bool IsLoadedSite(RadarSiteRow? row) =>
			row?.Site is { } s && _radar.HasRadarLoop && _radar.SelectedRadarOption?.Site == s;

		/// <summary>Latest scan time + age for the selected site. For the loaded site this IS the loop's
		/// own frame time, so the two readouts can't drift apart.</summary>
		public string LatestScanText
		{
			get
			{
				var t = IsLoadedSite(_selectedSite) ? _radar.NewestLoadedFrameTime : _fetchedScan?.ScanTime;
				if (t is not { } scan) return _scanStatus;
				var local = scan.ToLocalTime();
				return $"{local:g} ({FormatAge(DateTimeOffset.Now - local)})";
			}
		}

		/// <summary>VCP / scan-mode line for the selected site (from the loop when it's the loaded site).</summary>
		public string VcpModeText
		{
			get
			{
				if (IsLoadedSite(_selectedSite))
				{
					var mode = _radar.RadarModeText;
					return string.IsNullOrEmpty(mode) ? "—" : mode;
				}
				return string.IsNullOrEmpty(_fetchedScan?.ModeText) ? string.Empty : _fetchedScan!.ModeText!;
			}
		}

		// Re-raise the scan read-out (both derived properties).
		private void RaiseScan()
		{
			OnPropertyChanged(nameof(LatestScanText));
			OnPropertyChanged(nameof(VcpModeText));
		}

		private async Task LoadDetailAsync(RadarSiteRow? row, int token)
		{
			_fetchedScan = null;
			_scanStatus = string.Empty;
			RaiseScan();
			if (row is null)
			{
				IsLoadingScanInfo = false;
				return;
			}

			// The loop already knows this site's exact scan time + mode — read them off the VM (see the
			// scan-info fields) rather than paying for a second, staler lookup. No fetch, no spinner.
			if (IsLoadedSite(row))
			{
				IsLoadingScanInfo = false;
				RaiseScan();
				return;
			}

			IsLoadingScanInfo = true;
			try
			{
				var scan = await _radarService.GetLatestScanAsync(row.Site);
				if (token != _detailToken) return; // selection changed while we were fetching

				if (scan is null)
				{
					_scanStatus = "No recent data";
				}
				else
				{
					_fetchedScan = scan;
				}
				RaiseScan();
			}
			catch
			{
				if (token == _detailToken)
				{
					_scanStatus = "Unavailable";
					RaiseScan();
				}
			}
			finally
			{
				if (token == _detailToken)
				{
					IsLoadingScanInfo = false;
				}
			}
		}

		// ── Load on map ──────────────────────────────────────────────────────────────────────────
		/// <summary>Loads the selected site's radar loop on the map (same pipeline a marker click uses),
		/// flies to it, and closes the explorer. No-op with nothing selected.</summary>
		public void LoadOnMap()
		{
			if (_selectedSite?.Site is not { } site) return;

			var option = _radar.RadarOptions.FirstOrDefault(o => o.Site == site);
			if (option is not null)
			{
				_radar.SelectedRadarOption = option;
				// List-picked sites aren't on-screen like a marker click, so recenter on the site.
				_ = _mapService.FlyToAsync(site.Longitude, site.Latitude, 7);
			}
		}

		// Re-raises all detail-pane bindings after a selection change.
		private void RaiseDetail()
		{
			OnPropertyChanged(nameof(HasSelection));
			OnPropertyChanged(nameof(DetailId));
			OnPropertyChanged(nameof(DetailName));
			OnPropertyChanged(nameof(DetailClassLabel));
			OnPropertyChanged(nameof(DetailCoords));
			OnPropertyChanged(nameof(DetailStatusText));
			OnPropertyChanged(nameof(DetailDistanceText));
		}

		// Two largest non-zero units of an age span (yr/mo/day/hr/min) with an "ago" suffix,
		// e.g. "2 hr 5 min ago"; "just now" under a minute.
		private static string FormatAge(TimeSpan span)
		{
			if (span.TotalMinutes < 1) return "just now";
			double totalDays = span.TotalDays;
			int years = (int)(totalDays / 365.25);
			double remDays = totalDays - years * 365.25;
			int months = (int)(remDays / 30.44);
			int days = (int)(remDays - months * 30.44);
			var parts = new (int Value, string Unit)[]
			{
				(years, "yr"), (months, "mo"), (days, "day"), (span.Hours, "hr"), (span.Minutes, "min"),
			};
			var shown = parts.SkipWhile(p => p.Value == 0).Take(2).Where(p => p.Value > 0).ToList();
			return shown.Count == 0 ? "just now" : string.Join(" ", shown.Select(p => $"{p.Value} {p.Unit}")) + " ago";
		}

		private static double HaversineMiles(double lat1, double lon1, double lat2, double lon2)
		{
			const double R = 3958.7613; // Earth radius in miles
			double dLat = DegToRad(lat2 - lat1);
			double dLon = DegToRad(lon2 - lon1);
			double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
				Math.Cos(DegToRad(lat1)) * Math.Cos(DegToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
			return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
		}

		private static double DegToRad(double deg) => deg * Math.PI / 180.0;

		public event PropertyChangedEventHandler? PropertyChanged;

		private void OnPropertyChanged([CallerMemberName] string? name = null) =>
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
