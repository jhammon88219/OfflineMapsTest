using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OfflineMapsTest.Models
{
	/// <summary>
	/// A point marker shown on the map and editable in the "Selected Marker" tool window. Unlike the
	/// immutable domain records (<see cref="RadarSite"/> etc.) this is a <b>mutable, observable</b>
	/// entity, because its position changes when the user drags it. <see cref="Kind"/> distinguishes
	/// marker types; <see cref="CanDrag"/> / <see cref="IsSingleton"/> are capability data (not
	/// subclasses), so a new kind is just a new enum value + the flags it's created with.
	/// </summary>
	public sealed class MapMarker : INotifyPropertyChanged
	{
		public MapMarker(string id, MarkerKind kind, double latitude, double longitude, string label,
			LocationSource positionSource, bool canDrag = true, bool isSingleton = false)
		{
			Id = id;
			Kind = kind;
			_latitude = latitude;
			_longitude = longitude;
			_label = label;
			_positionSource = positionSource;
			CanDrag = canDrag;
			IsSingleton = isSingleton;
		}

		/// <summary>Stable identifier, also used as the JS-side marker id (drag/click correlation).</summary>
		public string Id { get; }

		/// <summary>What this marker represents (drives the editor's title + remove behavior).</summary>
		public MarkerKind Kind { get; }

		/// <summary>Whether the user can drag this marker to refine its position.</summary>
		public bool CanDrag { get; }

		/// <summary>Whether only one marker of this kind may exist at a time.</summary>
		public bool IsSingleton { get; }

		private double _latitude;
		public double Latitude
		{
			get => _latitude;
			set { if (_latitude != value) { _latitude = value; OnPropertyChanged(); } }
		}

		private double _longitude;
		public double Longitude
		{
			get => _longitude;
			set { if (_longitude != value) { _longitude = value; OnPropertyChanged(); } }
		}

		private string _label;
		public string Label
		{
			get => _label;
			set { if (_label != value) { _label = value; OnPropertyChanged(); } }
		}

		private LocationSource _positionSource;
		/// <summary>How the current position was set: the GPS/IP source when located, or
		/// <see cref="LocationSource.Manual"/> once the user drags it.</summary>
		public LocationSource PositionSource
		{
			get => _positionSource;
			set { if (_positionSource != value) { _positionSource = value; OnPropertyChanged(); } }
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private void OnPropertyChanged([CallerMemberName] string? name = null) =>
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
