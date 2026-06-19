using System.Globalization;
using RaycastPM.Models;

namespace RaycastPM.ViewModels;

public sealed class PlanCalendarDayItem : ObservableObject
{
    private bool _isSelected;

    public required DateTime Date { get; init; }

    public bool IsCurrentMonth { get; init; }

    public bool IsToday { get; init; }

    public required IReadOnlyList<PlanItem> Plans { get; init; }

    public required string SecondaryText { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string DayText => Date.Day.ToString(CultureInfo.InvariantCulture);

    public int PlanCount => Plans.Count;

    public bool HasPlans => PlanCount > 0;
}
