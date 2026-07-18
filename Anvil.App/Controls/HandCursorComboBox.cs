using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls
{
	/// <summary>
	/// A ComboBox that shows the hand ("I'm clickable") cursor on hover.
	///
	/// It exists as a subclass ONLY because of where WinUI keeps the cursor: <c>UIElement.ProtectedCursor</c>
	/// is protected, so it can't be reached from a Style, an attached property, or the host — deriving is the
	/// supported seam. It therefore adds NOTHING but the cursor: the Product selector's look stays entirely in
	/// <c>ProductRampComboBoxStyle</c>, and any other combo that wants the affordance can use this with its own
	/// style. The cursor covers the whole control (children inherit it), so the ramp reads as clickable too.
	/// </summary>
	public partial class HandCursorComboBox : ComboBox
	{
		public HandCursorComboBox()
		{
			ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
		}
	}
}
