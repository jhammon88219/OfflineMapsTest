using System;
using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// A fetched Level II volume ready to render: the local (virtual-host) URL of the
	/// cached <c>.V06</c> file, the site it belongs to, and the volume's scan time. The
	/// WebView fetches <see cref="LocalUrl"/>, decodes it, and projects the gates using the
	/// site's antenna coordinates. <see cref="ModeText"/> (live frames only) describes the
	/// radar's operating mode parsed from the volume — VCP number, precip vs clear-air, and
	/// how many 0.5° sweeps the volume has (the SAILS indicator). For <see cref="VolumeTime"/>,
	/// the live (chunks) path uses the actual radial collection time of the chosen 0.5° sweep,
	/// so it stays accurate when a mid-volume SAILS cut is fresher than the volume start.
	///
	/// <para><see cref="Tilts"/> lists the DISTINCT elevation angles the volume's VCP scans — the tilt
	/// choices the UI offers. It's read from the Message 5 elevation table, which the extraction copies
	/// into every tilt's buffer, so the full list is known from any single cached tilt with no extra
	/// fetch. Empty when the VCP doesn't parse (a legacy/raw volume), which the UI reads as "no tilt
	/// choice" rather than guessing. <see cref="TiltAngle"/> is which of those tilts this buffer holds;
	/// null means the base (lowest) tilt, whose cache file keeps the original un-suffixed name.</para>
	/// </summary>
	public record RadarVolume(
		string LocalUrl,
		RadarSite Site,
		DateTimeOffset VolumeTime,
		string? ModeText = null,
		IReadOnlyList<float>? Tilts = null,
		float? TiltAngle = null);
}
