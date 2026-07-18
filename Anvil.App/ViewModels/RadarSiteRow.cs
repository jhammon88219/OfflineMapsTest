using System.ComponentModel;
using System.Runtime.CompilerServices;
using Anvil.Models;

namespace Anvil.ViewModels
{
	/// <summary>
	/// A presentation row for the dock "Radar Sites" list. Wraps an immutable
	/// <see cref="RadarSite"/> with the observable, view-facing state the list needs so its rows
	/// can render the same states as the on-map site buttons: <see cref="IsOffline"/> drives the
	/// "down" (no recent data) look, and <see cref="IsAvailable"/> is bound to the row's IsEnabled
	/// so down rows aren't clickable. (Selection itself is the ListView's own SelectedItem state —
	/// the row only carries what selection can't express.)
	/// </summary>
	public sealed class RadarSiteRow : INotifyPropertyChanged
	{
		public RadarSiteRow(RadarSite site) => Site = site;

		public RadarSite Site { get; }
		public string Id => Site.Id;
		public string Name => Site.Name;

		/// <summary>Human label for the site's network, for the explorer's chip/detail (Operational / Research / TDWR).</summary>
		public string ClassLabel => Site.Class switch
		{
			RadarSiteClass.Research => "Research",
			RadarSiteClass.Tdwr => "TDWR",
			_ => "Operational",
		};

		/// <summary>Antenna coordinates for the explorer detail, e.g. "35.333, -97.278".</summary>
		public string Coords => $"{Site.Latitude:0.000}, {Site.Longitude:0.000}";

		/// <summary>Status label mirroring the marker state ("Online" / "Offline").</summary>
		public string StatusLabel => _isOffline ? "Offline" : "Online";

		private bool _isOffline;

		/// <summary>True when the site has no recent data in the feed; renders as a down row.</summary>
		public bool IsOffline
		{
			get => _isOffline;
			set
			{
				if (_isOffline == value) return;
				_isOffline = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(IsAvailable));
				OnPropertyChanged(nameof(StatusLabel));
			}
		}

		/// <summary>Inverse of <see cref="IsOffline"/>; bound to the row's IsEnabled (down = not clickable).</summary>
		public bool IsAvailable => !_isOffline;

		public event PropertyChangedEventHandler? PropertyChanged;

		private void OnPropertyChanged([CallerMemberName] string? name = null) =>
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
