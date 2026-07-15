using Microsoft.UI.Xaml;

namespace OfflineMapsTest.Controls
{
	/// <summary>
	/// Attached properties for <c>ProductRampComboBoxStyle</c> — the Product selector that draws each
	/// product's color ramp next to its name (so the selector doubles as the legend).
	///
	/// The style's ControlTemplate renders the selected product's <see cref="RampBar"/> in the CLOSED area,
	/// and that bar shows the live Inspect tick. But the inspect state lives on the view model, not on the
	/// ComboBox — and a template can only bind to its TemplatedParent. These attached properties are the
	/// seam: the host sets them on the ComboBox (from the VM), and the template reads them back off the
	/// TemplatedParent and forwards them to the RampBar.
	/// </summary>
	public static class ProductRampCombo
	{
		/// <summary>Where the Inspect marker sits along the selected product's ramp (0-1).</summary>
		public static readonly DependencyProperty InspectFractionProperty =
			DependencyProperty.RegisterAttached("InspectFraction", typeof(double), typeof(ProductRampCombo),
				new PropertyMetadata(0.0));

		public static double GetInspectFraction(DependencyObject obj) => (double)obj.GetValue(InspectFractionProperty);
		public static void SetInspectFraction(DependencyObject obj, double value) => obj.SetValue(InspectFractionProperty, value);

		/// <summary>Whether the Inspect marker is shown (inspect mode on + a value under the cursor).</summary>
		public static readonly DependencyProperty IsInspectVisibleProperty =
			DependencyProperty.RegisterAttached("IsInspectVisible", typeof(bool), typeof(ProductRampCombo),
				new PropertyMetadata(false));

		public static bool GetIsInspectVisible(DependencyObject obj) => (bool)obj.GetValue(IsInspectVisibleProperty);
		public static void SetIsInspectVisible(DependencyObject obj, bool value) => obj.SetValue(IsInspectVisibleProperty, value);
	}
}
