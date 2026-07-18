using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the SPC outlook subsystem: the day/product selection + overlay opacity,
	/// the issued/valid readout + info card + forecast-discussion narrative, the next-update progress
	/// bar, and the outlook background refresh loop. Extracted from MapViewModel; drives the map through
	/// <see cref="IMapService"/>. (SPC watch boxes are a separate subsystem — see
	/// <see cref="WatchesViewModel"/> — since they're current-conditions alerts, not a forecast.)
	/// </summary>
	public sealed class OutlookViewModel : INotifyPropertyChanged
	{
		private readonly IMapService _mapService;
		private readonly ISpcOutlookService _spcOutlookService;
		private readonly DispatcherQueue _dispatcher;

		// Readiness guard: outlook/watch commands only run once the map page has reported 'mapReady'
		// (set by OnMapsReadyAsync, called from MapViewModel.OnMapsReadyAsync).
		private bool _isMapReady;

		// Selected SPC outlook day + product option. The option list cascades to
		// whatever's valid for the selected day (plus a leading "None" entry); selecting
		// an option shows that product on the map, or clears it for None.
		private int _selectedDay;
		private DayOption? _selectedDayOption;
		private IReadOnlyList<OutlookOption> _productOptions = new List<OutlookOption>();
		private OutlookOption? _selectedOption;
		// Master "show outlook layer" gate (Outlook tool-window toggle). Defaults OFF so the app
		// launches with no outlook drawn; flipping it on shows the armed Day/Product selection.
		private bool _isOutlookVisible;

		// Authoritative issued/valid/expire readout for the loaded outlook, parsed from
		// the product's cached GeoJSON. Empty when None is selected or no times are known.
		private string _outlookTimesText = string.Empty;

		// Fill opacity (0-1) for the outlook polygons; the outlines stay opaque so the
		// basemap reads through. Driven by the ribbon's opacity slider.
		private double _outlookOpacity = 0.05;

		// Suppresses outlook map updates while a day-change cascade re-selects the
		// option (the product combobox transiently nulls its selection mid-swap).
		private bool _suppressOutlookUpdate;

		// Outlook refresh schedule (set by MainWindow each ~15-min cycle) for the Outlook tool
		// window's next-update progress bar.
		private DateTimeOffset? _outlookCycleStart;
		private DateTimeOffset? _nextOutlookRefreshAt;

		public OutlookViewModel(IMapService mapService, ISpcOutlookService spcOutlookService)
		{
			_mapService = mapService;
			_spcOutlookService = spcOutlookService;
			_dispatcher = DispatcherQueue.GetForCurrentThread();

			// SPC outlook selectors. Day 1 Categorical is the armed default, but the visibility toggle
			// (IsOutlookVisible) defaults off, so nothing is drawn on launch. Assign backing fields
			// directly so construction fires no map command (OnMapsReadyAsync applies state when ready).
			Days = BuildDayOptions(_spcOutlookService.AvailableDays);
			_selectedDayOption = Days.FirstOrDefault();
			_selectedDay = _selectedDayOption?.Day ?? 0;
			RebuildProductOptions();
			_selectedOption = DefaultOptionForDay();
		}

		/// <summary>Kicks off the outlook background refresh loop (called once at launch). Watches have
		/// their own loop — see <see cref="WatchesViewModel.StartBackgroundRefresh"/>.</summary>
		public void StartBackgroundRefresh()
		{
			_ = RefreshOutlooksInBackgroundAsync();
		}

		// SPC products update a handful of times a day at scheduled issuances; poll periodically so
		// we catch new ones. Conditional GETs make each cycle cheap (mostly 304s when unchanged).
		private static readonly TimeSpan OutlookRefreshInterval = TimeSpan.FromMinutes(15);

		private Task RefreshOutlooksInBackgroundAsync() => BackgroundRefresh.RunPeriodicAsync(OutlookRefreshInterval, async first =>
		{
			try
			{
				var results = await _spcOutlookService.RefreshAllAsync();
				var updated = results.Count(r => r.Status is SpcOutlookFetchStatus.Updated);
				var failed = results.Count(r => r.Status is SpcOutlookFetchStatus.FailedCacheKept
					or SpcOutlookFetchStatus.FailedNoCache);
				System.Diagnostics.Debug.WriteLine($"[SPC] refreshed {results.Count} products, {updated} updated, {failed} failed.");

				// Re-apply the current outlook on launch (so a first-run empty cache overlay appears and
				// the issued/valid readout picks up times) and whenever a cycle actually pulled new data —
				// but not when a periodic cycle was all 304s, so we don't needlessly re-render every 15 min.
				if (first || updated > 0)
				{
					_dispatcher.TryEnqueue(() => OnOutlooksRefreshed());
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[SPC] refresh aborted: {ex.Message}");
			}

			// Tell the next-update bar when the next periodic refresh is roughly due (fixed cadence;
			// the refresh itself takes seconds, negligible vs the 15-min interval). Runs every cycle.
			var cycleStart = DateTimeOffset.Now;
			_dispatcher.TryEnqueue(() => SetOutlookRefreshSchedule(cycleStart, cycleStart + OutlookRefreshInterval));
		});

		/// <summary>Outlook days that have products (1-8), each labeled with its date;
		/// static for the app lifetime.</summary>
		public IReadOnlyList<DayOption> Days { get; }

		/// <summary>
		/// The selected outlook day (carrying its date label). Changing it cascades the
		/// product list and auto-selects a product for the new day so an overlay stays
		/// visible.
		/// </summary>
		public DayOption? SelectedDayOption
		{
			get => _selectedDayOption;
			set
			{
				if (_selectedDayOption == value || value is null)
				{
					return;
				}

				_selectedDayOption = value;
				_selectedDay = value.Day;
				OnPropertyChanged();

				// Rebuild the option list, then re-select an option for the new day.
				// Suppress overlay updates during the swap (the product combobox briefly
				// nulls its selection as its items change) and push one update at the end.
				_suppressOutlookUpdate = true;
				RebuildProductOptions();
				SelectedOption = DefaultOptionForDay();
				_suppressOutlookUpdate = false;

				ApplyCurrentOutlook();
			}
		}

		/// <summary>
		/// Options for the product selector: a leading "None" entry (clears the overlay)
		/// followed by the products valid for <see cref="SelectedDay"/>.
		/// </summary>
		public IReadOnlyList<OutlookOption> ProductOptions => _productOptions;

		/// <summary>
		/// The selected option. Setting it shows that product on the map, or clears the
		/// overlay for the "None" option, once the map is ready.
		/// </summary>
		public OutlookOption? SelectedOption
		{
			get => _selectedOption;
			set
			{
				if (_selectedOption == value)
				{
					return;
				}

				_selectedOption = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(SelectedProduct));

				if (!_suppressOutlookUpdate)
				{
					ApplyCurrentOutlook();
				}
			}
		}

		/// <summary>The product behind the current selection (null for "None").</summary>
		public SpcOutlookProduct? SelectedProduct => _selectedOption?.Product;

		/// <summary>
		/// Master on/off gate for the outlook overlay (bound to the Outlook tool-window toggle).
		/// Independent of the Day/Product selection: when off, no outlook is drawn even if a
		/// product is selected; when on, the selected product (if any) is shown. Defaults off so
		/// the app launches with no outlook on the map.
		/// </summary>
		public bool IsOutlookVisible
		{
			get => _isOutlookVisible;
			set
			{
				if (_isOutlookVisible == value)
				{
					return;
				}

				_isOutlookVisible = value;
				OnPropertyChanged();
				ApplyCurrentOutlook();
			}
		}

		/// <summary>
		/// Authoritative "Issued … · Valid … → …" line for the loaded outlook, parsed from
		/// the product's cached GeoJSON (local time). Empty when None is selected or the
		/// cache has no times yet; bound to a readout that hides while empty.
		/// </summary>
		public string OutlookTimesText
		{
			get => _outlookTimesText;
			private set
			{
				if (_outlookTimesText == value)
				{
					return;
				}

				_outlookTimesText = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(HasOutlookTimes));
			}
		}

		/// <summary>Whether an issued/valid readout is available to show.</summary>
		public bool HasOutlookTimes => _outlookTimesText.Length > 0;

		// ── SPC outlook info card (shown while an outlook, not "None", is selected). Title +
		//    issued/effective times come from the cached GeoJSON; the forecast-discussion text is
		//    fetched lazily from SPC's HTML page (GetNarrativeAsync). All recomputed in
		//    UpdateOutlookCard when the selection changes or the outlook cache refreshes. ──
		private string _outlookCardTitle = string.Empty;
		private string _outlookIssuedText = string.Empty;
		private string _outlookValidText = string.Empty;
		private string _outlookNarrative = string.Empty;

		/// <summary>Whether an outlook is actually shown (a product selected AND the layer toggled
		/// on) — drives the Outlook Details window's visibility.</summary>
		public bool HasOutlookCard => _isOutlookVisible && _selectedOption?.Product is not null;

		/// <summary>Card header for the selected outlook, e.g. "Day 1 · Tornado".</summary>
		public string OutlookCardTitle => _outlookCardTitle;

		/// <summary>When the outlook was issued (local), or "—".</summary>
		public string OutlookIssuedText => _outlookIssuedText;

		/// <summary>The outlook's valid window (local), or "—".</summary>
		public string OutlookValidText => _outlookValidText;

		/// <summary>SPC forecast-discussion text for the selected outlook (or a status line).</summary>
		public string OutlookNarrativeText
		{
			get => _outlookNarrative;
			private set
			{
				if (_outlookNarrative == value)
				{
					return;
				}
				_outlookNarrative = value;
				OnPropertyChanged();
			}
		}



		/// <summary>Progress (0-100) toward the next SPC outlook refresh, for the Outlook bar.</summary>
		public double OutlookNextUpdateProgress => NextUpdate.ProgressOf(_outlookCycleStart, _nextOutlookRefreshAt);

		/// <summary>Countdown label to the next SPC outlook refresh (e.g. "next ~9 min").</summary>
		public string OutlookNextUpdateText => NextUpdate.CountdownOf(_nextOutlookRefreshAt);

		/// <summary>Called by MainWindow after each outlook refresh with the next refresh schedule.</summary>
		public void SetOutlookRefreshSchedule(DateTimeOffset cycleStart, DateTimeOffset next)
		{
			_outlookCycleStart = cycleStart;
			_nextOutlookRefreshAt = next;
			OnPropertyChanged(nameof(OutlookNextUpdateProgress));
			OnPropertyChanged(nameof(OutlookNextUpdateText));
		}

		// App-lifetime 1s tick that advances the outlook next-update progress bar (independent of any
		// radar loop, so the outlook bar updates even when no loop is active).
		private async Task RunProgressTickAsync()
		{
			using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
			while (await timer.WaitForNextTickAsync())
			{
				OnPropertyChanged(nameof(OutlookNextUpdateProgress));
				OnPropertyChanged(nameof(OutlookNextUpdateText));
			}
		}


		/// <summary>
		/// Fill opacity (0-1) of the outlook polygons. The outlines stay opaque, so
		/// lowering this lets the basemap and its borders read through the fill.
		/// </summary>
		public double OutlookOpacity
		{
			get => _outlookOpacity;
			set
			{
				if (_outlookOpacity == value)
				{
					return;
				}

				_outlookOpacity = value;
				OnPropertyChanged();

				if (_isMapReady)
				{
					_ = _mapService.SetOutlookOpacityAsync(value);
				}
			}
		}

		// Rebuilds the per-day option list ("None" + the day's products).
		private void RebuildProductOptions()
		{
			var options = new List<OutlookOption> { new("None", null) };
			options.AddRange(_spcOutlookService
				.GetProductsForDay(_selectedDay)
				.Select(p => new OutlookOption(p.TypeLabel, p)));
			_productOptions = options;
			OnPropertyChanged(nameof(ProductOptions));
		}

		// The option to show for the current day: its Categorical if present (days 1-3),
		// else the day's first real product (days 4-8 lead with Probabilistic).
		private OutlookOption DefaultOptionForDay() =>
			_productOptions.FirstOrDefault(o => o.Product?.Type == SpcOutlookType.Categorical)
			?? _productOptions.FirstOrDefault(o => o.Product is not null)
			?? _productOptions[0];

		// Pushes the current selection to the map (show product, or clear for None),
		// once the map is ready.
		private void ApplyCurrentOutlook()
		{
			if (!_isMapReady)
			{
				return;
			}

			var product = _selectedOption?.Product;
			if (product is not null && _isOutlookVisible)
			{
				_ = _mapService.ShowOutlookAsync(product);
			}
			else
			{
				_ = _mapService.ClearOutlookAsync();
			}

			UpdateOutlookTimes();
		}

		// Labels each outlook day with the calendar date it covers (Day N = today + N-1,
		// local), e.g. "Day 1 · Sat Jun 14". A close-enough mapping for orientation; the
		// authoritative issued/valid times come from the loaded GeoJSON (UpdateOutlookTimes).
		private static IReadOnlyList<DayOption> BuildDayOptions(IReadOnlyList<int> days)
		{
			var today = DateTime.Now.Date;
			return days
				.Select(d => new DayOption(d, $"Day {d} · {today.AddDays(d - 1):ddd MMM d}"))
				.ToList();
		}

		// Reads the issued/valid/expire times for the current product from its cached GeoJSON
		// (local time) and updates both the ribbon readout and the info card. The ribbon line is
		// cleared when None is selected or no times are available; the card follows the selection.
		private void UpdateOutlookTimes()
		{
			// When the layer is toggled off, treat the selection as "none" so the times readout,
			// the Outlook Details window, and HasOutlookCard all reflect what's actually on the map.
			var product = _isOutlookVisible ? _selectedOption?.Product : null;
			var times = product is null ? null : _spcOutlookService.GetTimesForProduct(product);

			if (times is null)
			{
				OutlookTimesText = string.Empty;
			}
			else
			{
				var parts = new List<string>(2);
				if (times.Issued is { } issued)
				{
					parts.Add($"Issued {issued.ToLocalTime():ddd h:mm tt}");
				}
				if (times.Valid is { } valid && times.Expire is { } expire)
				{
					parts.Add($"Valid {valid.ToLocalTime():ddd h:mm tt} → {expire.ToLocalTime():ddd h:mm tt}");
				}
				OutlookTimesText = string.Join("  ·  ", parts);
			}

			UpdateOutlookCard(product, times);
		}

		// Updates the SPC outlook info card: title + issued/effective from the cached times, and
		// kicks off the (lazy, disk-cached) forecast-discussion fetch. Cleared for "None".
		private void UpdateOutlookCard(SpcOutlookProduct? product, SpcOutlookTimes? times)
		{
			if (product is null)
			{
				_outlookCardTitle = string.Empty;
				_outlookIssuedText = string.Empty;
				_outlookValidText = string.Empty;
				_narrativeFor = null;
				OutlookNarrativeText = string.Empty;
			}
			else
			{
				_outlookCardTitle = $"Day {product.Day} · {product.TypeLabel}";
				_outlookIssuedText = times?.Issued is { } iss
					? iss.ToLocalTime().ToString("ddd MMM d · h:mm tt")
					: "—";
				_outlookValidText = times?.Valid is { } v && times?.Expire is { } e
					? $"{v.ToLocalTime():ddd h:mm tt} → {e.ToLocalTime():ddd h:mm tt}"
					: "—";
				_ = RefreshOutlookNarrativeAsync(product);
			}

			OnPropertyChanged(nameof(HasOutlookCard));
			OnPropertyChanged(nameof(OutlookCardTitle));
			OnPropertyChanged(nameof(OutlookIssuedText));
			OnPropertyChanged(nameof(OutlookValidText));
		}

		// The product the current narrative belongs to — so a same-product refresh updates the
		// text silently, while a product switch shows the "Loading…" placeholder.
		private SpcOutlookProduct? _narrativeFor;

		// Fetches the SPC forecast discussion (network → disk cache) and shows it, unless the user
		// changed selection while it loaded.
		private async Task RefreshOutlookNarrativeAsync(SpcOutlookProduct product)
		{
			if (!ReferenceEquals(_narrativeFor, product))
			{
				OutlookNarrativeText = "Loading forecast discussion…";
			}

			string? text = null;
			try
			{
				text = await _spcOutlookService.GetNarrativeAsync(product);
			}
			catch
			{
				// Best effort; fall through to the not-available message.
			}

			if (!ReferenceEquals(_selectedOption?.Product, product))
			{
				return; // selection changed mid-fetch
			}
			_narrativeFor = product;
			OutlookNarrativeText = text ?? "Forecast discussion isn't available for this product yet.";
		}

		/// <summary>
		/// Called after the launch outlook refresh finishes: re-applies the current
		/// selection so a first-run (empty cache) overlay appears and the issued/valid
		/// readout picks up the freshly-written times.
		/// </summary>
		public void OnOutlooksRefreshed() => ApplyCurrentOutlook();

		/// <summary>Called by MapViewModel once the map page is ready: applies the startup outlook state
		/// and starts the next-update progress tick.</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;

			// Show the selected outlook only if the visibility toggle is on (it defaults off, so the
			// app launches with no outlook); sync the fill opacity to the slider's initial value.
			var startupProduct = _selectedOption?.Product;
			if (startupProduct is not null && _isOutlookVisible)
			{
				await _mapService.ShowOutlookAsync(startupProduct);
			}
			await _mapService.SetOutlookOpacityAsync(_outlookOpacity);
			UpdateOutlookTimes();

			// Drive the SPC outlook next-update progress bar.
			_ = RunProgressTickAsync();
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}
}
