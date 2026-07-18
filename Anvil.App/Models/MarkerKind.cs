namespace Anvil.Models
{
	/// <summary>
	/// The type of a <see cref="MapMarker"/>. Extensible (search result, saved pin, …); today only
	/// the user's location marker exists. Per-kind rules (singleton, draggable) are carried as data
	/// on the marker rather than as subclasses, so adding a kind needs no new type.
	/// </summary>
	public enum MarkerKind
	{
		/// <summary>The user's current/refined location — a singleton, draggable marker.</summary>
		UserLocation
	}
}
