using System;

namespace Anvil.Models
{
	/// <summary>
	/// The issued / valid / expire times of a loaded SPC outlook, parsed from its GeoJSON.
	/// Convective products carry all three (the <c>*_ISO</c> properties); fire-weather
	/// products carry only valid/expire, so <see cref="Issued"/> may be null. Any field is
	/// null when the cached file is missing, empty (no risk areas), or lacks that property.
	/// </summary>
	public record SpcOutlookTimes(
		DateTimeOffset? Issued,
		DateTimeOffset? Valid,
		DateTimeOffset? Expire);
}
