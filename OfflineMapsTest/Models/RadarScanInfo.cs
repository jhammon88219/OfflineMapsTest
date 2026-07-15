using System;

namespace OfflineMapsTest.Models
{
	/// <summary>
	/// The freshest scan known for a site — the Radar Site Explorer's detail read-out.
	///
	/// <see cref="ScanTime"/> is taken from whichever feed is FRESHER: the near-real-time chunks bucket
	/// (~1-2 min, what the loop's live frame uses) or the archive bucket (~5-10 min behind, the loop's
	/// history frames). Reporting only the archive made the explorer read minutes staler than the loop
	/// showing the same site. <see cref="ModeText"/> (the VCP line) is parsed from the cached archive tilt.
	/// </summary>
	public record RadarScanInfo(DateTimeOffset ScanTime, string? ModeText);
}
