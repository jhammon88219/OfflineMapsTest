using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for map markers + user location: the "My Location" locate action (transient
	/// status) and the selectable/draggable marker entity (the Selected Marker readout). Extracted
	/// from MapViewModel; drives the map through <see cref="IMapService"/>.
	/// </summary>
	public sealed class MarkersViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly ILocationService _locationService;

		// Readiness guard: marker JS commands only run once the map page has reported 'mapReady'
		// (flipped by SetMapReady, called from MapViewModel.OnMapsReadyAsync).
		private bool _isMapReady;

		public MarkersViewModel(IMapService mapService, ILocationService locationService)
		{
			_mapService = mapService;
			_locationService = locationService;
		}

		/// <summary>Marks the map page ready (markers are user-action-driven, so there is no startup
		/// work — this just enables the JS pushes).</summary>
		public void SetMapReady() => _isMapReady = true;

		// ════════════════════════════════════════════════════════════════════════════════════════
		//  MAP MARKERS + USER LOCATION
		//  Self-contained block — the whole feature can be peeled out by deleting this region, the
		//  MapMarker/MarkerKind models, the map.js marker shims, and the "Selected Marker" tool
		//  window. Two concerns, kept separate on purpose:
		//    1. The "My Location" button: a transient locate ACTION + its in-progress/failure text
		//       (the Map card shows nothing persistent on success — see #2).
		//    2. The marker ENTITY: a MapMarker placed on the map, selectable + draggable, whose
		//       standing readout (coords + how its position was set) lives in the Selected Marker
		//       tool window. Drag updates flow JS → C# only (drag-only); manual coordinate entry /
		//       address search would add the C# → JS push (window.moveMarker) + a sync guard later.
		// ════════════════════════════════════════════════════════════════════════════════════════

		// Stable id shared with the JS marker (window.showUserLocation tags drag/click messages with it).
		private const string UserMarkerId = "user";

		// All markers currently on the map (today only the singleton user-location marker). A plain
		// list — nothing binds to it yet; the UI works off SelectedMarker. Promote to an observable
		// collection if/when a marker list view is added.
		private readonly List<MapMarker> _markers = new();

		/// <summary>The current user-location marker, if one has been placed (else null). Exposed so the
		/// Radar Site Explorer can compute distance-to-site; read-only view over the private list.</summary>
		public MapMarker? UserLocationMarker => _markers.FirstOrDefault(m => m.Kind == MarkerKind.UserLocation);

		// ── 1. Locate action (Map card) ──
		private bool _isLocating;
		private string _locateStatus = string.Empty; // transient only: "Locating…" / "Location unavailable"

		/// <summary>Whether a location resolve is in flight (drives the button spinner + disabled state).</summary>
		public bool IsLocating
		{
			get => _isLocating;
			private set
			{
				if (SetProperty(ref _isLocating, value))
				{
					OnPropertyChanged(nameof(CanLocate));
				}
			}
		}

		/// <summary>Inverse of <see cref="IsLocating"/> — the My Location button is enabled when idle.</summary>
		public bool CanLocate => !_isLocating;

		/// <summary>Transient locate status: "Locating…" while running, "Location unavailable" on
		/// failure, empty on success (the Selected Marker window owns the standing readout).</summary>
		public string LocateStatusText => _locateStatus;

		/// <summary>Whether there's transient locate text to show.</summary>
		public bool HasLocateStatus => _locateStatus.Length > 0;

		private void SetLocateStatus(string text)
		{
			if (SetProperty(ref _locateStatus, text, nameof(LocateStatusText)))
			{
				OnPropertyChanged(nameof(HasLocateStatus));
			}
		}

		/// <summary>
		/// Resolves the user's location (OS → IP fallback), drops/refreshes the singleton user-location
		/// marker, recenters on it, and selects it (so the Selected Marker window appears). No-op while
		/// already locating. On success the Map card status clears; the source readout is on the marker.
		/// </summary>
		public async Task ShowMyLocationAsync()
		{
			if (_isLocating)
			{
				return;
			}

			IsLocating = true;
			SetLocateStatus("Locating…");
			try
			{
				var location = await _locationService.ResolveAsync();
				if (location is null)
				{
					SetLocateStatus("Location unavailable");
					return;
				}

				var label = string.IsNullOrWhiteSpace(location.Description) ? "Your location" : location.Description!;
				var marker = UpsertUserLocationMarker(location.Latitude, location.Longitude, label, location.Source);

				if (_isMapReady)
				{
					// Call single-quotes the label without escaping; drop apostrophes (e.g. "Coeur d'Alene").
					await _mapService.ShowUserLocationAsync(location.Longitude, location.Latitude, label.Replace("'", string.Empty));
					await _mapService.FlyToAsync(location.Longitude, location.Latitude, 8);
				}

				SelectedMarker = marker;
				SetLocateStatus(string.Empty); // success: the Selected Marker window shows the result
			}
			finally
			{
				IsLocating = false;
			}
		}

		// Singleton enforcement lives here (not in the type): drop any existing user-location marker
		// and add the fresh one. Returns the new marker so the caller can select it.
		private MapMarker UpsertUserLocationMarker(double latitude, double longitude, string label, LocationSource source)
		{
			_markers.RemoveAll(m => m.Kind == MarkerKind.UserLocation);
			var marker = new MapMarker(UserMarkerId, MarkerKind.UserLocation, latitude, longitude, label,
				source, canDrag: true, isSingleton: true);
			_markers.Add(marker);
			return marker;
		}

		// ── 2. Marker entity (Selected Marker tool window) ──
		private MapMarker? _selectedMarker;

		/// <summary>The marker whose editor is shown (null = none). Set by a locate, a marker click,
		/// or cleared on remove.</summary>
		public MapMarker? SelectedMarker
		{
			get => _selectedMarker;
			private set
			{
				if (SetProperty(ref _selectedMarker, value))
				{
					OnPropertyChanged(nameof(HasSelectedMarker));
					RaiseSelectedMarker();
				}
			}
		}

		/// <summary>Whether a marker is selected (drives the Selected Marker tool window's visibility).</summary>
		public bool HasSelectedMarker => _selectedMarker is not null;

		/// <summary>Editor title from the marker kind, e.g. "My Location".</summary>
		public string SelectedMarkerKindLabel => _selectedMarker?.Kind switch
		{
			MarkerKind.UserLocation => "My Location",
			_ => "Marker"
		};

		/// <summary>The marker's descriptive label (e.g. the resolved place name), or empty.</summary>
		public string SelectedMarkerSubtitle => _selectedMarker?.Label ?? string.Empty;

		/// <summary>The selected marker's coordinates, formatted (or empty).</summary>
		public string SelectedMarkerCoords => _selectedMarker is { } m
			? $"{m.Latitude:0.0000}, {m.Longitude:0.0000}"
			: string.Empty;

		/// <summary>How the selected marker's position was set — drives the editor's source icon/color.</summary>
		public LocationSource SelectedMarkerSource => _selectedMarker?.PositionSource ?? LocationSource.None;

		/// <summary>Friendly source label for the editor, e.g. "Device GPS" / "Manually adjusted".</summary>
		public string SelectedMarkerSourceText => _selectedMarker?.PositionSource switch
		{
			LocationSource.OperatingSystem => "Device GPS",
			LocationSource.IpAddress => "IP estimate (approximate)",
			LocationSource.Manual => "Manually adjusted",
			_ => string.Empty
		};

		/// <summary>Whether the selected marker can be dragged (shows the "drag to refine" hint).</summary>
		public bool CanDragSelectedMarker => _selectedMarker?.CanDrag ?? false;

		/// <summary>Whether the selected marker is the singleton user-location marker — drives the
		/// "Your location" badge + the re-detect/reset note in the editor.</summary>
		public bool SelectedMarkerIsUserLocation => _selectedMarker?.Kind == MarkerKind.UserLocation;

		private void RaiseSelectedMarker()
		{
			OnPropertyChanged(nameof(SelectedMarkerKindLabel));
			OnPropertyChanged(nameof(SelectedMarkerSubtitle));
			OnPropertyChanged(nameof(SelectedMarkerCoords));
			OnPropertyChanged(nameof(SelectedMarkerSource));
			OnPropertyChanged(nameof(SelectedMarkerSourceText));
			OnPropertyChanged(nameof(CanDragSelectedMarker));
			OnPropertyChanged(nameof(SelectedMarkerIsUserLocation));
		}

		/// <summary>A marker on the map was clicked (from JS): select it so its editor shows.</summary>
		public void OnMarkerClicked(string? id)
		{
			var marker = _markers.FirstOrDefault(m => m.Id == id);
			if (marker is not null)
			{
				SelectedMarker = marker;
			}
		}

		/// <summary>A marker was dragged (from JS): record the refined position and flag it manual.</summary>
		public void OnMarkerMoved(string? id, double longitude, double latitude)
		{
			var marker = _markers.FirstOrDefault(m => m.Id == id);
			if (marker is null)
			{
				return;
			}
			marker.Latitude = latitude;
			marker.Longitude = longitude;
			marker.PositionSource = LocationSource.Manual; // dragged → no longer the GPS/IP fix
			if (ReferenceEquals(marker, _selectedMarker))
			{
				RaiseSelectedMarker();
			}
		}

		/// <summary>Removes the selected marker from the map and the model, and clears the selection.</summary>
		public void RemoveSelectedMarker()
		{
			if (_selectedMarker is not { } marker)
			{
				return;
			}
			_markers.Remove(marker);
			if (marker.Kind == MarkerKind.UserLocation && _isMapReady)
			{
				_ = _mapService.ClearUserLocationAsync();
			}
			SelectedMarker = null;
		}
	}
}
