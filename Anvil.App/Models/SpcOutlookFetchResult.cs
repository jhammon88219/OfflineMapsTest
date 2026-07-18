namespace Anvil.Models
{
	/// <summary>Outcome of refreshing one product during RefreshAllAsync.</summary>
	public enum SpcOutlookFetchStatus
	{
		Updated,          // fetched; cache rewritten with newer data
		NotModified,      // server returned 304; existing cache kept
		FailedCacheKept,  // fetch failed but a previous cache file remains usable
		FailedNoCache     // fetch failed and there is no cached file to fall back to
	}

	/// <summary>Per-product result returned from RefreshAllAsync (never thrown).</summary>
	public record SpcOutlookFetchResult(
		string ProductId,
		SpcOutlookFetchStatus Status,
		string? Message = null);
}
