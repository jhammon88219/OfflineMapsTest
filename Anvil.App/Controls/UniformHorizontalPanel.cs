using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Anvil.Controls
{
	/// <summary>
	/// Lays its children out left-to-right in EQUAL widths that fill the available width — WinUI 3 has no
	/// built-in UniformGrid, and a horizontal StackPanel sizes to content. Used as the segmented scrubber's
	/// ItemsPanel so N frame cells always split the track evenly (and restretch on resize). Height matches
	/// the tallest child (the cells are uniform, so any of them).
	/// </summary>
	public sealed partial class UniformHorizontalPanel : Panel
	{
		protected override Size MeasureOverride(Size availableSize)
		{
			var n = Children.Count;
			var cellWidth = (n > 0 && !double.IsInfinity(availableSize.Width)) ? availableSize.Width / n : 0;
			double height = 0;
			foreach (var child in Children)
			{
				child.Measure(new Size(cellWidth, availableSize.Height));
				if (child.DesiredSize.Height > height) height = child.DesiredSize.Height;
			}
			var width = double.IsInfinity(availableSize.Width) ? cellWidth * n : availableSize.Width;
			return new Size(width, height);
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			var n = Children.Count;
			if (n == 0) return finalSize;
			var cellWidth = finalSize.Width / n;
			for (var i = 0; i < n; i++)
			{
				// Rounded left edges so cumulative fractional widths don't drift or leave a seam.
				var x0 = System.Math.Round(i * cellWidth);
				var x1 = System.Math.Round((i + 1) * cellWidth);
				Children[i].Arrange(new Rect(x0, 0, x1 - x0, finalSize.Height));
			}
			return finalSize;
		}
	}
}
