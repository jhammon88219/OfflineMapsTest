namespace Anvil.Models
{
	/// <summary>
	/// Outcome of fetching + caching a day's SPC storm reports (Tornado / Wind / Hail) for the verification
	/// overlay (see <see cref="Anvil.Services.IStormReportService.EnsureReportsAsync"/>). The per-type counts
	/// drive the card readouts. A valid-but-empty day (SPC published the file with no reports yet) reports
	/// <see cref="Found"/> = true with zero counts and no error.
	/// </summary>
	/// <param name="Found">True if at least one report file was fetched (or a cached file was reused), so the
	/// GeoJSON on disk reflects that convective day — even if the day had zero reports.</param>
	/// <param name="Tornado">Number of tornado reports written.</param>
	/// <param name="Wind">Number of wind reports written.</param>
	/// <param name="Hail">Number of hail reports written.</param>
	/// <param name="Error">A human-readable failure reason, or null on success.</param>
	public sealed record StormReportResult(
		bool Found,
		int Tornado,
		int Wind,
		int Hail,
		string? Error);
}
