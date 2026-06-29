using System.ComponentModel;
using System.Runtime.CompilerServices;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.ViewModels
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
			}
		}

		/// <summary>Inverse of <see cref="IsOffline"/>; bound to the row's IsEnabled (down = not clickable).</summary>
		public bool IsAvailable => !_isOffline;

		public event PropertyChangedEventHandler? PropertyChanged;

		private void OnPropertyChanged([CallerMemberName] string? name = null) =>
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
