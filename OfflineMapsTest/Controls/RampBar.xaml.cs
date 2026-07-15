using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OfflineMapsTest.Converters;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Controls
{
	/// <summary>
	/// A radar product's color scale as a compact bar (see RampBar.xaml): the ramp, its min/max values, the
	/// live Inspect tick, and a hover read-out of the value under the pointer. Used by
	/// <c>ProductRampComboBoxStyle</c> so the Product selector doubles as the legend.
	/// </summary>
	public sealed partial class RampBar : UserControl
	{
		public RampBar()
		{
			InitializeComponent();
		}

		/// <summary>The product's color ramp (pushed from the WebView's radar-ramps.js). Null = ghost bar.</summary>
		public RadarRampInfo? Ramp
		{
			get => (RadarRampInfo?)GetValue(RampProperty);
			set => SetValue(RampProperty, value);
		}

		public static readonly DependencyProperty RampProperty =
			DependencyProperty.Register(nameof(Ramp), typeof(RadarRampInfo), typeof(RampBar),
				new PropertyMetadata(null, OnRampChanged));

		// The bar's look is x:Bind'd to Ramp/HasRamp; HasRamp is derived, so re-raise it when Ramp changes.
		private static void OnRampChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((RampBar)d).Bindings.Update();

		/// <summary>Whether a real ramp is known (drives ghost vs. real bar).</summary>
		public bool HasRamp => Ramp?.Stops is { Count: > 0 };

		/// <summary>Whether to show the min/max values under the bar. Off for the compact dropdown rows.</summary>
		public bool ShowScale
		{
			get => (bool)GetValue(ShowScaleProperty);
			set => SetValue(ShowScaleProperty, value);
		}

		public static readonly DependencyProperty ShowScaleProperty =
			DependencyProperty.Register(nameof(ShowScale), typeof(bool), typeof(RampBar), new PropertyMetadata(false));

		/// <summary>Where the Inspect marker sits along the ramp (0-1).</summary>
		public double InspectFraction
		{
			get => (double)GetValue(InspectFractionProperty);
			set => SetValue(InspectFractionProperty, value);
		}

		public static readonly DependencyProperty InspectFractionProperty =
			DependencyProperty.Register(nameof(InspectFraction), typeof(double), typeof(RampBar), new PropertyMetadata(0.0));

		/// <summary>
		/// The <c>Margin</c> a SIBLING needs so that centering it lands on the RAMP STRIP's centre rather than
		/// on this control's overall centre. With <see cref="ShowScale"/> on, the min/max row hangs below the
		/// strip and drags the shared centre down — which is what left the Product combo's short name and
		/// chevron sitting low against the ramp. A NEGATIVE top inset shifts a centered sibling up by half its
		/// magnitude, which is exactly the strip's offset from centre; going negative (rather than a positive
		/// bottom inset, which shifts by the same half) keeps the sibling's desired height from stretching the
		/// row taller. Measured from live layout — font metrics, DPI and ShowScale all land in it — so a
		/// template never hardcodes a guess at this control's internals. It's 0 when ShowScale is off (the
		/// compact dropdown rows), where the strip already IS the whole control.
		/// </summary>
		public Thickness StripAlignMargin
		{
			get => (Thickness)GetValue(StripAlignMarginProperty);
			private set => SetValue(StripAlignMarginProperty, value);
		}

		public static readonly DependencyProperty StripAlignMarginProperty =
			DependencyProperty.Register(nameof(StripAlignMargin), typeof(Thickness), typeof(RampBar),
				new PropertyMetadata(default(Thickness)));

		// Whatever this control has below the strip (the gap + the scale row) is what pulls its centre off
		// the strip's, so the nudge IS that difference. Re-measured on either size change.
		private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e) =>
			StripAlignMargin = new Thickness(0, -Math.Max(0, Root.ActualHeight - BarHost.ActualHeight), 0, 0);

		/// <summary>Whether the Inspect marker is shown (inspect mode on + a value under the cursor).</summary>
		public bool IsInspectVisible
		{
			get => (bool)GetValue(IsInspectVisibleProperty);
			set => SetValue(IsInspectVisibleProperty, value);
		}

		public static readonly DependencyProperty IsInspectVisibleProperty =
			DependencyProperty.Register(nameof(IsInspectVisible), typeof(bool), typeof(RampBar), new PropertyMetadata(false));

		// ── x:Bind helpers ───────────────────────────────────────────────────────────────────────
		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		public Visibility GhostVisibility(RadarRampInfo? ramp) =>
			ramp?.Stops is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;

		public Brush RampBrush(RadarRampInfo? ramp) => RampToBrushConverter.ToBrush(ramp);

		public GridLength LeftStar(double fraction) =>
			new(Math.Clamp(fraction, 0, 1), GridUnitType.Star);

		public GridLength RightStar(double fraction) =>
			new(1 - Math.Clamp(fraction, 0, 1), GridUnitType.Star);

		public string ScaleMin(RadarRampInfo? ramp) => ValueText(ramp, 0, withUnit: false);
		public string ScaleMax(RadarRampInfo? ramp) => ValueText(ramp, 1, withUnit: true);

		// ── Hover read-out ───────────────────────────────────────────────────────────────────────
		// Reading the value under the pointer is why this is a control: a Style can't carry pointer logic.
		// The tooltip content is rewritten as the pointer moves, so it tracks the position along the ramp.
		private void OnBarPointerMoved(object sender, PointerRoutedEventArgs e)
		{
			if (Ramp is not { Stops.Count: > 0 } ramp || BarHost.ActualWidth <= 0)
			{
				return;
			}

			double x = e.GetCurrentPoint(BarHost).Position.X;
			double fraction = Math.Clamp(x / BarHost.ActualWidth, 0, 1);
			ToolTipService.SetToolTip(BarHost, ValueText(ramp, fraction, withUnit: true));
		}

		private void OnBarPointerExited(object sender, PointerRoutedEventArgs e) =>
			ToolTipService.SetToolTip(BarHost, null);

		// The ramp value at `fraction` (0-1). Velocity is shown in mph (converted from the ramp's native
		// m/s, matching the inspector); other products keep their native unit (dBZ, ρHV, °/km, dB).
		private static string ValueText(RadarRampInfo? ramp, double fraction, bool withUnit)
		{
			if (ramp is null)
			{
				return string.Empty;
			}

			double v = ramp.Min + (ramp.Max - ramp.Min) * fraction;
			string unit = ramp.Unit;
			if (unit == "m/s")
			{
				v *= 2.23694;
				unit = "mph";
			}

			string num = v.ToString("0.##", CultureInfo.InvariantCulture);
			return withUnit && unit.Length > 0 ? $"{num} {unit}" : num;
		}
	}
}
