namespace Anvil.Models
{
	/// <summary>
	/// The network a radar site belongs to. These are mutually exclusive, so the category is one enum
	/// rather than independent flags (a site can't be both research and TDWR) — the type makes the
	/// invalid combination unrepresentable.
	/// </summary>
	public enum RadarSiteClass
	{
		/// <summary>An operational WSR-88D in the NWS network; shown by default.</summary>
		Operational,
		/// <summary>A research/test radar (e.g. the ROC test bed KCRI); behind the "Show Research Radars" toggle.</summary>
		Research,
		/// <summary>An FAA Terminal Doppler Weather Radar (the <c>T***</c> network); behind the "Show TDWRs" toggle.</summary>
		Tdwr,
	}

	/// <summary>
	/// A radar site selectable in the picker. <see cref="Latitude"/> / <see cref="Longitude"/> are the
	/// antenna location, used both to frame the map and to project the decoded Level II gates in the
	/// WebView. <see cref="Class"/> is the network it belongs to; research/TDWR sites are hidden behind
	/// their own toggles but otherwise load and render through the exact same pipeline as an operational
	/// site (TDWR volumes are the same AR2V0008 Archive Level II family). Mirrors the immutable-record
	/// style of <see cref="MapRegion"/> / <see cref="MapStyle"/>.
	/// </summary>
	public record RadarSite(string Id, string Name, double Latitude, double Longitude,
		RadarSiteClass Class = RadarSiteClass.Operational);
}
