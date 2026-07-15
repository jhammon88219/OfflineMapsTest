using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OfflineMapsTest.Controls
{
	/// <summary>
	/// Chrome-only shell for the bottom overlay bar: the centered show/hide pull-tab, the theme-aware
	/// surface + a hairline card border (<c>CardStrokeColorDefaultSolidBrush</c>, same as the settings
	/// cards) that runs along the bar's top edge and wraps up and around the tab, and the bottom-overlay
	/// collapse behavior. The host fills <see cref="BarContent"/> with the actual controls, so section
	/// content is composed in MainWindow. The show/hide state is pure view state and lives here, not on a
	/// view model. (Named for what it is — a bottom overlay shell — not for what it hosts; the actual radar
	/// transport lives in the RadarControls section it hosts.)
	/// </summary>
	public sealed partial class OverlayBar : UserControl
	{
		public OverlayBar()
		{
			InitializeComponent();
		}

		/// <summary>The content shown inside the bar (filled by the host — e.g. the section controls).</summary>
		public object? BarContent
		{
			get => GetValue(BarContentProperty);
			set => SetValue(BarContentProperty, value);
		}

		public static readonly DependencyProperty BarContentProperty =
			DependencyProperty.Register(nameof(BarContent), typeof(object), typeof(OverlayBar), new PropertyMetadata(null));

		/// <summary>Whether the bar is shown (the pull-tab toggles it). Pure view state.</summary>
		public bool IsOverlayBarVisible
		{
			get => (bool)GetValue(IsOverlayBarVisibleProperty);
			set => SetValue(IsOverlayBarVisibleProperty, value);
		}

		public static readonly DependencyProperty IsOverlayBarVisibleProperty =
			DependencyProperty.Register(nameof(IsOverlayBarVisible), typeof(bool), typeof(OverlayBar), new PropertyMetadata(true));

		// x:Bind function mapping a bool to Visibility (no value-converter lookup needed).
		public Visibility VisibleWhen(bool value) =>
			value ? Visibility.Visible : Visibility.Collapsed;

		// Pull-tab glyph/label: down chevron + "Hide" when showing, up chevron + "Show" when hidden.
		//  = ChevronDown,  = ChevronUp (Segoe Fluent Icons).
		public string ToggleGlyph(bool visible) => visible ? "" : "";

		public string ToggleLabel(bool visible) => visible ? "Hide" : "Show";

		private void OnToggleOverlayBarClick(object sender, RoutedEventArgs e) =>
			IsOverlayBarVisible = !IsOverlayBarVisible;
	}
}
