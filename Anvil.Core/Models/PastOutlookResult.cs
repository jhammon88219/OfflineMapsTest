using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// Outcome of fetching + caching a historical SPC outlook issuance for the PastCast window (see
	/// <see cref="Anvil.Services.ISpcOutlookService.EnsurePastOutlookAsync"/>). Historical outlooks are
	/// immutable, so a found result is cached on disk and never refreshed.
	/// </summary>
	/// <param name="Found">True if the issuance existed and at least one product's polygons were written.</param>
	/// <param name="CycleUsed">The UTC issuance cycle actually fetched (echoes the requested one).</param>
	/// <param name="AvailableTypes">The product types that had polygons this issuance (drives which
	/// product options are live in the card).</param>
	/// <param name="Times">Issued / valid / expire read from the issuance, or null if unknown.</param>
	/// <param name="Error">A human-readable failure reason, or null on success (including a valid-but-empty
	/// issuance, which reports <see cref="Found"/> = false with no error).</param>
	public sealed record PastOutlookResult(
		bool Found,
		int CycleUsed,
		IReadOnlyList<SpcOutlookType> AvailableTypes,
		SpcOutlookTimes? Times,
		string? Error);
}
