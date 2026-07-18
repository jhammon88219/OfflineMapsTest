using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the DOW Event Viewer: a single curated mobile-radar (Doppler-on-Wheels) frame
	/// rendered through the SAME radar render path as the NEXRAD loop, but standalone — no loop / live /
	/// site machinery (see tools/dow_import.py). Extracted from RadarViewModel and owned by it, so the
	/// shared radar-display / color-scale gate can react to <see cref="IsShowing"/>.
	/// </summary>
	public sealed class DowViewModel : INotifyPropertyChanged
	{
		private readonly IMapService _mapService;
		private readonly IDowEventProvider _dowEventProvider;

		private int _dowEventIndex;
		private string _dowStatus = string.Empty;
		private int _dowProductIndex; // 0 = reflectivity, 1 = velocity
		private bool _isShowing;

		public DowViewModel(IMapService mapService, IDowEventProvider dowEventProvider)
		{
			_mapService = mapService;
			_dowEventProvider = dowEventProvider;
			DowEvents = _dowEventProvider.GetEvents();
		}

		/// <summary>Whether a DOW frame is currently displayed (separate from a NEXRAD loop). RadarViewModel
		/// watches this to gate the shared radar-display / color-scale state.</summary>
		public bool IsShowing
		{
			get => _isShowing;
			private set { if (_isShowing != value) { _isShowing = value; OnPropertyChanged(); } }
		}

		/// <summary>Called by RadarViewModel when a NEXRAD site selection takes over the radar layer.</summary>
		public void OnNexradTookOver() => IsShowing = false;

		/// <summary>The curated DOW frames available to view (empty until events are converted + bundled).</summary>
		public IReadOnlyList<DowEvent> DowEvents { get; }

		/// <summary>True when at least one DOW event is bundled (drives the tool window's enabled state).</summary>
		public bool HasDowEvents => DowEvents.Count > 0;

		/// <summary>Selected DOW event (index into <see cref="DowEvents"/>).</summary>
		public int DowEventIndex
		{
			get => _dowEventIndex;
			set
			{
				var c = DowEvents.Count == 0 ? 0 : Math.Clamp(value, 0, DowEvents.Count - 1);
				if (_dowEventIndex != c) { _dowEventIndex = c; OnPropertyChanged(); }
			}
		}

		/// <summary>Rendered DOW moment: 0 = reflectivity, 1 = velocity. Applies live to a shown frame.</summary>
		public int DowProductIndex
		{
			get => _dowProductIndex;
			set
			{
				var c = Math.Clamp(value, 0, 1);
				if (_dowProductIndex == c) return;
				_dowProductIndex = c;
				OnPropertyChanged();
				_ = _mapService.SetRadarProductAsync(c == 1 ? "velocity" : "reflectivity");
			}
		}

		/// <summary>Product choices for the DOW Event Viewer (matches <see cref="DowProductIndex"/>).</summary>
		public IReadOnlyList<string> DowProductOptions { get; } = new[] { "Reflectivity", "Velocity" };

		/// <summary>Transient status line for the DOW Event Viewer.</summary>
		public string DowStatus
		{
			get => _dowStatus;
			private set { if (_dowStatus != value) { _dowStatus = value; OnPropertyChanged(); } }
		}

		/// <summary>Loads + shows the selected DOW frame (decoded in the WebView via the radar pipeline).</summary>
		public async Task LoadDowEventAsync()
		{
			if (DowEvents.Count == 0)
			{
				DowStatus = "No DOW events bundled yet — convert one with tools/dow_import.py and add it to Assets/DowEvents.";
				return;
			}

			var ev = DowEvents[Math.Clamp(_dowEventIndex, 0, DowEvents.Count - 1)];
			DowStatus = $"Loading {ev.Label}…";
			try
			{
				await _mapService.ShowDowFrameAsync(ev.Url);
				await _mapService.SetRadarProductAsync(_dowProductIndex == 1 ? "velocity" : "reflectivity");
				DowStatus = $"Showing {ev.Label}";
				IsShowing = true; // RadarViewModel re-raises HasRadarDisplay/HasColorScale off this
			}
			catch (Exception ex)
			{
				DowStatus = $"Load failed: {ex.Message}";
			}
		}

		/// <summary>Clears the shown DOW frame.</summary>
		public async Task ClearDowEventAsync()
		{
			await _mapService.ClearDowFrameAsync();
			IsShowing = false;
			DowStatus = string.Empty;
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}
}
