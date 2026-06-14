using System;

namespace OfflineMapsTest.Models
{
	/// <summary>
	/// A fetched Level II volume ready to render: the local (virtual-host) URL of the
	/// cached <c>.V06</c> file, the site it belongs to, and the volume's scan time. The
	/// WebView fetches <see cref="LocalUrl"/>, decodes it, and projects the gates using the
	/// site's antenna coordinates.
	/// </summary>
	public record RadarVolume(string LocalUrl, RadarSite Site, DateTimeOffset VolumeTime);
}
