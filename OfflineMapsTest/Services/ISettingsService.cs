namespace OfflineMapsTest.Services
{
	/// <summary>
	/// App settings persisted across launches (packaged-app <c>LocalSettings</c>). Today just the
	/// folder holding the offline basemap PMTiles file; built to grow as more settings appear.
	/// </summary>
	public interface ISettingsService
	{
		/// <summary>The bundled basemap file the app expects inside <see cref="MapDataFolder"/>.</summary>
		string MapDataFileName { get; }

		/// <summary>
		/// Folder mapped to the <c>mapdata</c> WebView host (where <see cref="MapDataFileName"/> lives).
		/// Defaults to a runtime-resolved Desktop folder (never a hardcoded path) when unset.
		/// </summary>
		string MapDataFolder { get; set; }

		/// <summary>Whether the basemap file is present in the given folder (or the current one if null).</summary>
		bool MapDataFilePresent(string? folder = null);
	}
}
