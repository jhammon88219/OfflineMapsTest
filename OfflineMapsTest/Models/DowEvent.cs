namespace OfflineMapsTest.Models
{
	/// <summary>
	/// A curated, pre-converted mobile-radar (Doppler on Wheels / DOW) frame available to the DOW
	/// Event Viewer. The frame is an offline-curated <c>.dow.json</c> (see <c>tools/dow_import.py</c>)
	/// served from the <c>dowevents</c> virtual host and rendered through the normal radar pipeline.
	/// </summary>
	/// <param name="FileName">The bundled file name, e.g. <c>goshen_2009-06-05_DOW7.dow.json</c>.</param>
	/// <param name="Label">A human label for the picker (derived from the file name).</param>
	/// <param name="Url">The <c>https://dowevents/&lt;file&gt;</c> URL the WebView fetches.</param>
	public sealed record DowEvent(string FileName, string Label, string Url);
}
