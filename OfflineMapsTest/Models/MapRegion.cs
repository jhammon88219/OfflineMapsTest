namespace OfflineMapsTest.Models
{
	/// <summary>
	/// A selectable map region. Center (<see cref="Longitude"/>/<see cref="Latitude"/>) +
	/// <see cref="Zoom"/> frame the main map. Only CONUS is used today.
	/// </summary>
	public record MapRegion(
		string Id,
		string DisplayName,
		double Longitude,
		double Latitude,
		double Zoom);
}
