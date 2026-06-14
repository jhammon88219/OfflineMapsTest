using System.Collections.Generic;
using OfflineMapsTest.Models;
using Windows.Networking.Sockets;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Default <see cref="IStyleProvider"/>. Hardcodes the available styles for
	/// now; both files are served locally via the "mapassets" virtual host.
	/// </summary>
	public sealed class StyleProvider : IStyleProvider
	{
		public IReadOnlyList<MapStyle> GetStyles() => new[]
		{
			new MapStyle("regular", "Regular", "style.json"),
			new MapStyle("dark", "Dark", "style-dark.json"),
			new MapStyle("dataVizlight", "Data Viz Light", "style-dataVizLight.json"),
			new MapStyle("dataVizBlack", "Data Viz Black", "style-dataVizBlack.json"),
			new MapStyle("dataVizGrayscale", "Data Viz Grayscale", "style-datVizGrayscale.json")
		};
	}
}
