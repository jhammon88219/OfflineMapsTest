using Anvil.Models;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One entry in the outlook product selector: a display label plus the product it
	/// selects. A null <see cref="Product"/> is the "None" entry, which clears the
	/// overlay — keeping the "None" choice in the UI layer without a sentinel leaking
	/// into the SPC domain model.
	/// </summary>
	public record OutlookOption(string Label, SpcOutlookProduct? Product);
}
