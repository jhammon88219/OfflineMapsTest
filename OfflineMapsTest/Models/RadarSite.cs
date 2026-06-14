namespace OfflineMapsTest.Models
{
	/// <summary>
	/// A WSR-88D radar site selectable in the picker. <see cref="Latitude"/> /
	/// <see cref="Longitude"/> are the antenna location, used both to frame the map and to
	/// project the decoded Level II gates in the WebView. Mirrors the immutable-record
	/// style of <see cref="MapRegion"/> / <see cref="MapStyle"/>.
	/// </summary>
	public record RadarSite(string Id, string Name, double Latitude, double Longitude, double Zoom);
}
