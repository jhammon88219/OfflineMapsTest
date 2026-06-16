using System;

namespace OfflineMapsTest.Models
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
	/// </summary>
	public record RadarVolume(string LocalUrl, RadarSite Site, DateTimeOffset VolumeTime, string? ModeText = null);
}
