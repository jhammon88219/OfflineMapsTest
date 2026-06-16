namespace OfflineMapsTest.ViewModels
{
	/// <summary>
	/// One entry in the outlook Day selector: the SPC day number plus a display label that
	/// pairs it with the calendar date it covers (e.g. "Day 1 · Sat Jun 14"). The label is
	/// computed from the current date (Day N = today + N-1); the authoritative issued/valid
	/// times come separately from the loaded product's GeoJSON.
	/// </summary>
	public record DayOption(int Day, string Label);
}
