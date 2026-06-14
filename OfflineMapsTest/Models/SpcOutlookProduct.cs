namespace OfflineMapsTest.Models
{
	/// <summary>
	/// A single SPC outlook product the app can display: a (day, type) pairing with a
	/// stable cache filename and the local URL the MapLibre page will load it from.
	/// Mirrors the immutable-record style of <see cref="MapStyle"/> / <see cref="MapRegion"/>.
	/// Remote source URLs live in the editable <c>SpcOutlookCatalog</c>, not here.
	/// </summary>
	public record SpcOutlookProduct(
		string Id,
		int Day,
		SpcOutlookType Type,
		string DisplayName,
		string CacheFileName,
		string LocalUrl)
	{
		/// <summary>
		/// Short, day-agnostic label for the product selector (the day is already
		/// chosen in the adjacent control, so "Day 1 Categorical" would be redundant).
		/// </summary>
		public string TypeLabel => Type switch
		{
			SpcOutlookType.Categorical => "Categorical",
			SpcOutlookType.Tornado => "Tornado",
			SpcOutlookType.Wind => "Wind",
			SpcOutlookType.Hail => "Hail",
			SpcOutlookType.ProbabilisticCombined => "Probabilistic",
			SpcOutlookType.ExtendedProbabilistic => "Probabilistic",
			SpcOutlookType.FireWeather => "Fire Weather",
			SpcOutlookType.ExtendedFireWeather => "Fire Weather",
			_ => Type.ToString()
		};
	}
}
