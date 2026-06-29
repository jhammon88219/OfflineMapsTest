namespace OfflineMapsTest.Models
{
	/// <summary>
	/// How a <see cref="UserLocation"/> was obtained — drives the UI affordance that shows
	/// which method produced the marker. Ordered from most to least precise.
	/// </summary>
	public enum LocationSource
	{
		/// <summary>No location resolved (neither method succeeded, or none attempted yet).</summary>
		None,
		/// <summary>OS geolocation (Windows.Devices.Geolocation) — GPS/Wi-Fi, accurate.</summary>
		OperatingSystem,
		/// <summary>IP-based geolocation (network lookup) — city-level, approximate fallback.</summary>
		IpAddress,
		/// <summary>Position set by the user dragging the marker (refined by hand).</summary>
		Manual
	}

	/// <summary>
	/// A resolved user location. <paramref name="Source"/> records which method produced it so
	/// the UI can indicate device-GPS vs IP-approximate; <paramref name="AccuracyMeters"/> is the
	/// reported accuracy when known (OS only) and <paramref name="Description"/> a human label
	/// (e.g. "Norman, Oklahoma" for an IP lookup). Immutable-record style like the other models.
	/// </summary>
	public record UserLocation(double Latitude, double Longitude, LocationSource Source, double? AccuracyMeters, string? Description);
}
