using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace OfflineMapsTest.Controls
{
	/// <summary>
	/// Chrome-only shell for the bottom transport bar: the centered show/hide pull-tab, the accent
	/// drop shadow, the theme-aware surface, and the bottom-overlay collapse behavior. The host fills
	/// <see cref="BarContent"/> with the actual controls, so section content is composed in MainWindow.
	/// The show/hide state is pure view state and lives here, not on a view model.
	/// </summary>
	public sealed partial class TransportBar : UserControl
	{
		// Soft accent glows behind the bar and the tab, drawn into one container visual hosted by
		// ShadowHost (which sits below the tab in z-order, so no glow ever crosses in front of it).
		private ContainerVisual? _glowRoot;
		private SpriteVisual? _barGlow;
		private DropShadow? _barShadow;
		private SpriteVisual? _tabGlow;
		private DropShadow? _tabShadow;

		// Watches for OS accent-color changes (ColorValuesChanged also covers light/dark switches).
		// Kept as a field so the subscription isn't garbage-collected.
		private readonly UISettings _uiSettings = new();

		public TransportBar()
		{
			InitializeComponent();
			Loaded += OnLoaded;
			SizeChanged += OnLayoutChanged;         // reposition the centered tab glow on window resize
			ActualThemeChanged += OnActualThemeChanged;
			_uiSettings.ColorValuesChanged += OnColorValuesChanged;
		}

		/// <summary>The content shown inside the bar (filled by the host — e.g. the section controls).</summary>
		public object? BarContent
		{
			get => GetValue(BarContentProperty);
			set => SetValue(BarContentProperty, value);
		}

		public static readonly DependencyProperty BarContentProperty =
			DependencyProperty.Register(nameof(BarContent), typeof(object), typeof(TransportBar), new PropertyMetadata(null));

		/// <summary>Whether the bar is shown (the pull-tab toggles it). Pure view state.</summary>
		public bool IsTransportBarVisible
		{
			get => (bool)GetValue(IsTransportBarVisibleProperty);
			set => SetValue(IsTransportBarVisibleProperty, value);
		}

		public static readonly DependencyProperty IsTransportBarVisibleProperty =
			DependencyProperty.Register(nameof(IsTransportBarVisible), typeof(bool), typeof(TransportBar), new PropertyMetadata(true));

		// x:Bind function mapping a bool to Visibility (no value-converter lookup needed).
		public Visibility VisibleWhen(bool value) =>
			value ? Visibility.Visible : Visibility.Collapsed;

		// Pull-tab glyph/label: down chevron + "Hide" when showing, up chevron + "Show" when hidden.
		//  = ChevronDown,  = ChevronUp (Segoe Fluent Icons).
		public string ToggleGlyph(bool visible) => visible ? "" : "";

		public string ToggleLabel(bool visible) => visible ? "Hide" : "Show";

		private void OnToggleTransportBarClick(object sender, RoutedEventArgs e) =>
			IsTransportBarVisible = !IsTransportBarVisible;

		private void OnLoaded(object sender, RoutedEventArgs e) => EnsureShadow();

		private void OnLayoutChanged(object sender, SizeChangedEventArgs e) => EnsureShadow();

		// Re-tint both glows when the OS switches light/dark (accent variant differs per theme).
		private void OnActualThemeChanged(FrameworkElement sender, object args) => ReTintGlows();

		// Fires when the system accent color (or theme) changes — on a background thread, so marshal
		// back to the UI thread before touching the composition objects.
		private void OnColorValuesChanged(UISettings sender, object args) =>
			DispatcherQueue?.TryEnqueue(ReTintGlows);

		private void ReTintGlows()
		{
			Color color = AccentShadowColor();
			if (_barShadow != null) _barShadow.Color = color;
			if (_tabShadow != null) _tabShadow.Color = color;
		}

		/// <summary>
		/// Attaches (or refreshes) soft accent-colored Composition drop shadows behind the bar and the
		/// tab, each sized/positioned to its element. Built once, then re-tinted + re-placed on layout
		/// or theme changes. Drawn in ShadowHost, which is below the tab in z-order.
		/// </summary>
		private void EnsureShadow()
		{
			Compositor compositor = ElementCompositionPreview.GetElementVisual(ShadowHost).Compositor;

			if (_glowRoot == null)
			{
				_glowRoot = compositor.CreateContainerVisual();

				_barShadow = CreateGlow(compositor);
				_barGlow = compositor.CreateSpriteVisual();
				_barGlow.Shadow = _barShadow;

				_tabShadow = CreateGlow(compositor);
				_tabGlow = compositor.CreateSpriteVisual();
				_tabGlow.Shadow = _tabShadow;

				_glowRoot.Children.InsertAtTop(_barGlow);
				_glowRoot.Children.InsertAtTop(_tabGlow);
				ElementCompositionPreview.SetElementChildVisual(ShadowHost, _glowRoot);
			}

			Color color = AccentShadowColor();
			_barShadow!.Color = color;
			_tabShadow!.Color = color;

			PlaceGlow(_barGlow!, BarBorder);
			PlaceGlow(_tabGlow!, TabButton);
		}

		// A soft glow: generous blur, biased slightly upward so it rises onto the map above the bar.
		private static DropShadow CreateGlow(Compositor compositor)
		{
			DropShadow shadow = compositor.CreateDropShadow();
			shadow.BlurRadius = 28f;
			shadow.Opacity = 0.55f;
			shadow.Offset = new Vector3(0f, -2f, 0f);
			return shadow;
		}

		// Size + position a glow sprite to match an element's bounds in ShadowHost coordinates. A
		// zero-size element (e.g. the collapsed bar) yields no glow. No mask is needed: each sprite's
		// rectangle is hidden behind its opaque element, so only the soft edge bleed shows.
		private void PlaceGlow(SpriteVisual sprite, FrameworkElement element)
		{
			if (element.ActualWidth <= 0 || element.ActualHeight <= 0 || element.Visibility == Visibility.Collapsed)
			{
				sprite.Size = Vector2.Zero;
				return;
			}

			Point origin = element.TransformToVisual(ShadowHost).TransformPoint(new Point(0, 0));
			sprite.Offset = new Vector3((float)origin.X, (float)origin.Y, 0f);
			sprite.Size = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);
		}

		// The theme-aware accent color for the glow, read LIVE from UISettings (so it tracks OS accent
		// changes — the app resource brushes don't refresh reliably): the lightened accent variant on
		// dark, the darkened one on light, so it reads on either backdrop.
		private Color AccentShadowColor()
		{
			UIColorType type = ActualTheme == ElementTheme.Light ? UIColorType.AccentDark1 : UIColorType.AccentLight2;
			return _uiSettings.GetColorValue(type);
		}
	}
}
