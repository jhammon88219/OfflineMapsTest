using OfflineMapsTest.Models;

namespace OfflineMapsTest.ViewModels
{
	/// <summary>
	/// One entry in the radar site selector: a display label plus the site it selects.
	/// A null <see cref="Site"/> is the "None" entry, which clears the radar layer.
	/// </summary>
	public record RadarOption(string Label, RadarSite? Site);
}
