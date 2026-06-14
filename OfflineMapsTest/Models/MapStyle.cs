namespace OfflineMapsTest.Models
{
	/// <summary>
	/// A selectable map style. <see cref="FileName"/> is the style file served
	/// via the "mapassets" virtual host, e.g. "style-dark.json".
	/// </summary>
	public record MapStyle(string Id, string DisplayName, string FileName);
}
