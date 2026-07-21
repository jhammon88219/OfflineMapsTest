using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The HISTORICAL SPC outlook overlay for the PastCast window — an independent overlay (like the live
	/// outlook in NowCast) keyed to the replay date/time held by <see cref="RadarViewModel"/>. Selecting a
	/// product fetches that day's archived issuance (via <see cref="ISpcOutlookService.EnsurePastOutlookAsync"/>,
	/// IEM archive back to ~2002) and shows it through the SAME map layer the live outlook uses. The map has
	/// one outlook layer, so <see cref="MapViewModel"/> hands ownership to this VM in past mode and back to
	/// <see cref="OutlookViewModel"/> otherwise.
	///
	/// Days 1-3 (product list cascades: Day 1-2 = Categorical/Tornado/Wind/Hail/Fire, Day 3 = Categorical/
	/// Fire). Issuance cycle is auto (the one in effect at the replay time for Day 1) with an override
	/// dropdown. Drives the map through <see cref="IMapService"/>.
	/// </summary>
	public sealed class PastOutlookViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly ISpcOutlookService _outlookService;
		private readonly RadarViewModel _radar;

		private bool _isMapReady;
		private int _applyToken; // guards a stale async apply when the selection changes mid-fetch

		public PastOutlookViewModel(IMapService mapService, ISpcOutlookService outlookService, RadarViewModel radar)
		{
			_mapService = mapService;
			_outlookService = outlookService;
			_radar = radar;

			Days = new[] { new DayOption(1, "Day 1"), new DayOption(2, "Day 2"), new DayOption(3, "Day 3") };
			_selectedDayOption = Days[0];
			RebuildProductOptions();
			_selectedProductOption = ProductOptions[0]; // None
			RebuildCycleOptions();
			_selectedCycleOption = CycleOptions[0]; // Auto

			// Keep the overlay in sync with the replay date/time controls (same card): if a product is up,
			// re-fetch for the new date. Mode enter/leave is driven by MapViewModel (it also owns the live
			// outlook), so we don't handle IsPastEventMode here.
			_radar.PropertyChanged += OnRadarChanged;
		}

		// ── Day (1-3) ──

		public IReadOnlyList<DayOption> Days { get; }

		private DayOption _selectedDayOption;
		public DayOption SelectedDayOption
		{
			get => _selectedDayOption;
			set
			{
				if (value is null || _selectedDayOption == value) return;
				SetProperty(ref _selectedDayOption, value);

				// Cascade the product + cycle lists to the new day, keeping the product type if still valid.
				var keepType = _selectedProductOption?.Type;
				RebuildProductOptions();
				_selectedProductOption = ProductOptions.FirstOrDefault(o => o.Type == keepType) ?? ProductOptions[0];
				OnPropertyChanged(nameof(SelectedProductOption));
				RebuildCycleOptions();
				_selectedCycleOption = CycleOptions[0];
				OnPropertyChanged(nameof(SelectedCycleOption));

				_ = ApplyAsync();
			}
		}

		private int SelectedDay => _selectedDayOption.Day;

		// ── Product (None + the day's products) ──

		private IReadOnlyList<PastOutlookOption> _productOptions = Array.Empty<PastOutlookOption>();
		public IReadOnlyList<PastOutlookOption> ProductOptions => _productOptions;

		private PastOutlookOption _selectedProductOption;
		public PastOutlookOption SelectedProductOption
		{
			get => _selectedProductOption;
			set
			{
				if (value is null || _selectedProductOption == value) return;
				SetProperty(ref _selectedProductOption, value);
				_ = ApplyAsync();
			}
		}

		private void RebuildProductOptions()
		{
			var opts = new List<PastOutlookOption> { new("None", null) };
			opts.Add(new("Categorical", SpcOutlookType.Categorical));
			if (SelectedDay == 1)
			{
				// Only Day 1 breaks out the individual hazards; Days 2-3 carry a single combined
				// "ANY SEVERE" probabilistic instead (SPC's product structure).
				opts.Add(new("Tornado", SpcOutlookType.Tornado));
				opts.Add(new("Wind", SpcOutlookType.Wind));
				opts.Add(new("Hail", SpcOutlookType.Hail));
			}
			else
			{
				opts.Add(new("Probabilistic", SpcOutlookType.ProbabilisticCombined));
			}
			opts.Add(new("Fire Weather", SpcOutlookType.FireWeather));
			_productOptions = opts;
			OnPropertyChanged(nameof(ProductOptions));
		}

		// ── Issuance cycle (Auto + the day's issuances) ──

		private IReadOnlyList<PastCycleOption> _cycleOptions = Array.Empty<PastCycleOption>();
		public IReadOnlyList<PastCycleOption> CycleOptions => _cycleOptions;

		private PastCycleOption _selectedCycleOption;
		public PastCycleOption SelectedCycleOption
		{
			get => _selectedCycleOption;
			set
			{
				if (value is null || _selectedCycleOption == value) return;
				SetProperty(ref _selectedCycleOption, value);
				_ = ApplyAsync();
			}
		}

		private void RebuildCycleOptions()
		{
			var opts = new List<PastCycleOption> { new("Auto", null) };
			opts.AddRange(CandidateCycles(SelectedDay).Select(c => new PastCycleOption($"{c:D2}Z", c)));
			_cycleOptions = opts;
			OnPropertyChanged(nameof(CycleOptions));
		}

		// ── Opacity ──

		private double _opacity = 0.15;
		public double Opacity
		{
			get => _opacity;
			set
			{
				if (Math.Abs(_opacity - value) < 1e-9) return;
				_opacity = value;
				OnPropertyChanged();
				if (_isMapReady) _ = _mapService.SetOutlookOpacityAsync(value);
			}
		}

		// ── Readouts ──

		private string _statusText = string.Empty;
		public string StatusText
		{
			get => _statusText;
			private set => SetProperty(ref _statusText, value);
		}

		private string _timesText = string.Empty;
		public string TimesText
		{
			get => _timesText;
			private set
			{
				if (SetProperty(ref _timesText, value))
				{
					OnPropertyChanged(nameof(HasTimes));
				}
			}
		}

		public bool HasTimes => _timesText.Length > 0;

		// ── Lifecycle / coordination (called by MapViewModel) ──

		/// <summary>Called once the map page is ready.</summary>
		public Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			return _radar.IsPastEventMode ? ApplyAsync() : Task.CompletedTask;
		}

		/// <summary>MapViewModel hands the shared outlook layer to/from this VM as PastCast toggles.
		/// Entering past mode shows the selected historical outlook; leaving clears the layer.</summary>
		public void OnPastModeChanged(bool on)
		{
			if (on) { _ = ApplyAsync(); }
			else { _ = ClearAsync(); }
		}

		private void OnRadarChanged(object? sender, PropertyChangedEventArgs e)
		{
			// A replay date/time change should move the outlook with it, but only when we own the layer
			// (past mode) and a product is actually shown.
			if (e.PropertyName is nameof(RadarViewModel.PastEventYearIndex)
				or nameof(RadarViewModel.PastEventMonthIndex)
				or nameof(RadarViewModel.PastEventDayIndex)
				or nameof(RadarViewModel.PastEventTime))
			{
				if (_radar.IsPastEventMode && _selectedProductOption.Type is not null)
				{
					_ = ApplyAsync();
				}
			}
		}

		// The core: fetch (with cycle fallback) + show the selected historical product, or clear for None.
		private async Task ApplyAsync()
		{
			if (!_isMapReady || !_radar.IsPastEventMode) return;

			var token = ++_applyToken;
			var type = _selectedProductOption.Type;
			if (type is null)
			{
				await _mapService.ClearOutlookAsync();
				StatusText = string.Empty;
				TimesText = string.Empty;
				return;
			}

			var day = SelectedDay;
			var date = ConvectiveDay(ReplayStartUtc());
			StatusText = "Loading outlook…";

			var (result, cycleUsed) = await EnsureWithFallbackAsync(date, day);
			if (token != _applyToken) return; // a newer selection won

			if (result is null || result.Error is not null)
			{
				await _mapService.ClearOutlookAsync();
				StatusText = result?.Error is { } err ? $"Outlook fetch failed: {err}" : "Outlook fetch failed.";
				TimesText = string.Empty;
				return;
			}
			if (!result.Found || !result.AvailableTypes.Contains(type.Value))
			{
				await _mapService.ClearOutlookAsync();
				StatusText = result.Found
					? $"No {_selectedProductOption.Label} in the {cycleUsed:D2}Z issuance for {date:MMM d, yyyy}."
					: $"No archived outlook for {date:MMM d, yyyy} (Day {day}).";
				TimesText = string.Empty;
				return;
			}

			var url = $"https://{SpcOutlookService.CacheHostName}/{SpcOutlookService.PastCacheName(date, day, cycleUsed, type.Value)}";
			var product = new SpcOutlookProduct(
				Id: $"past-{date:yyyyMMdd}-d{day}-c{cycleUsed:D2}-{type}",
				Day: day, Type: type.Value, DisplayName: _selectedProductOption.Label,
				CacheFileName: SpcOutlookService.PastCacheName(date, day, cycleUsed, type.Value),
				LocalUrl: url);

			await _mapService.ShowOutlookAsync(product);
			await _mapService.SetOutlookOpacityAsync(_opacity);

			StatusText = $"{_selectedProductOption.Label} · Day {day} · {cycleUsed:D2}Z · {date:MMM d, yyyy}";
			TimesText = FormatTimes(result.Times);
		}

		private async Task ClearAsync()
		{
			if (!_isMapReady) return;
			await _mapService.ClearOutlookAsync();
			StatusText = string.Empty;
			TimesText = string.Empty;
		}

		// Honors an explicit cycle override; otherwise tries the auto cycle first, then the day's remaining
		// issuances, using the first that actually has data (historical days vary in which cycles exist).
		private async Task<(PastOutlookResult? Result, int CycleUsed)> EnsureWithFallbackAsync(DateOnly date, int day)
		{
			if (_selectedCycleOption.Cycle is { } forced)
			{
				return (await _outlookService.EnsurePastOutlookAsync(date, day, forced), forced);
			}

			PastOutlookResult? last = null;
			var order = OrderedAutoCycles(day, ReplayStartUtc());
			foreach (var c in order)
			{
				var r = await _outlookService.EnsurePastOutlookAsync(date, day, c);
				last = r;
				if (r.Found || r.Error is not null) return (r, c);
			}
			return (last, order.Length > 0 ? order[0] : 0);
		}

		// ── Replay-date + issuance-cycle resolution ──

		// Reconstructs the replay window's UTC start from the RadarViewModel date/time controls (mirrors
		// RadarViewModel's own composition: local midnight + start time-of-day, then to UTC).
		private DateTimeOffset ReplayStartUtc()
		{
			var year = _radar.PastEventYearOptions[_radar.PastEventYearIndex];
			var month = _radar.PastEventMonthIndex + 1;
			var day = Math.Min(_radar.PastEventDayIndex + 1, DateTime.DaysInMonth(year, month));
			var localMidnight = new DateTimeOffset(year, month, day, 0, 0, 0,
				TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day)));
			return (localMidnight + _radar.PastEventTime).ToUniversalTime();
		}

		// The SPC "convective day" (12Z→12Z) containing the replay start = the IEM `valid` date.
		private static DateOnly ConvectiveDay(DateTimeOffset startUtc)
		{
			var d = startUtc.UtcDateTime;
			return DateOnly.FromDateTime(d.Hour >= 12 ? d : d.AddDays(-1));
		}

		// Issuance cycles that exist for a day (UTC hour; 16 = the 1630Z update). Day 1 has the full set;
		// forecast days have fewer. Used for the override dropdown and the auto fallback order.
		private static int[] CandidateCycles(int day) => day switch
		{
			1 => new[] { 13, 16, 20, 1, 6 },
			2 => new[] { 17, 6 }, // Day 2 primary issuance is 1730Z (cycle 17); 06Z as fallback
			_ => new[] { 8 },     // Day 3 issued ~0730Z (cycle 8)
		};

		// Auto order: for Day 1, the issuance in effect at the replay time first, then the rest as fallback;
		// for forecast days, the standard order.
		private static int[] OrderedAutoCycles(int day, DateTimeOffset startUtc)
		{
			var all = CandidateCycles(day);
			if (day != 1) return all;

			var convDay = ConvectiveDay(startUtc).ToDateTime(TimeOnly.MinValue);
			// Absolute issuance instants across the 12Z→12Z window.
			var instants = new (int Cycle, DateTime AtUtc)[]
			{
				(13, convDay.AddHours(13)),
				(16, convDay.AddHours(16.5)), // 1630Z
				(20, convDay.AddHours(20)),
				(1,  convDay.AddDays(1).AddHours(1)),
				(6,  convDay.AddDays(1).AddHours(6)),
			};
			var inEffect = instants.Where(i => i.AtUtc <= startUtc.UtcDateTime)
				.OrderByDescending(i => i.AtUtc).Select(i => i.Cycle).FirstOrDefault(13);
			return new[] { inEffect }.Concat(all.Where(c => c != inEffect)).ToArray();
		}

		private static string FormatTimes(SpcOutlookTimes? times)
		{
			if (times is null) return string.Empty;
			var parts = new List<string>(2);
			if (times.Issued is { } iss) parts.Add($"Issued {iss.ToLocalTime():ddd MMM d h:mm tt}");
			if (times.Valid is { } v && times.Expire is { } e)
				parts.Add($"Valid {v.ToLocalTime():ddd h:mm tt} → {e.ToLocalTime():ddd h:mm tt}");
			return string.Join("  ·  ", parts);
		}
	}

	/// <summary>One entry in the PastCast outlook product picker (None carries a null type).</summary>
	public sealed record PastOutlookOption(string Label, SpcOutlookType? Type);

	/// <summary>One entry in the issuance-cycle picker (Auto carries a null cycle).</summary>
	public sealed record PastCycleOption(string Label, int? Cycle);
}
