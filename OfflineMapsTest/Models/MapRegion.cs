namespace OfflineMapsTest.Models
{
	/// <summary>
	/// A selectable map region the user can jump to. Center/Zoom drive the main-map
	/// flyTo; InsetZoom is the wider zoom used when this region is framed in a small
	/// inset box. MinZoom/MaxZoom constrain zooming and West/South/East/North define
	/// the pan-limit (max-bounds) box, applied via <c>MapService.GoToRegionAsync</c>.
	/// </summary>
	public record MapRegion(
		string Id,
		string DisplayName,
		double Longitude,
		double Latitude,
		double Zoom,
		double InsetZoom,
		double MinZoom,
		double MaxZoom,
		double West,
		double South,
		double East,
		double North);
}
