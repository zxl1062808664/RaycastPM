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

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string DayText => Date.Day.ToString(CultureInfo.InvariantCulture);

    public int PlanCount => Plans.Count;

    public bool HasPlans => PlanCount > 0;

    public bool HasOverflowPlans => PlanCount > 1;

    public string OverflowText => HasOverflowPlans ? $"+{PlanCount - 1}" : string.Empty;

    public PlanItem? PrimaryPlan => Plans.FirstOrDefault();

    public string CountText => $"{PlanCount}项";

    public string PrimaryPlanSubtitle
    {
        get
        {
            if (PrimaryPlan is null)
            {
                return string.Empty;
            }

            if (PrimaryPlan.TargetCompletedAt is { } targetCompletedAt && targetCompletedAt.Date == Date)
            {
                return $"计划 {targetCompletedAt:HH:mm}";
            }

            if (PrimaryPlan.StartAt.Date == Date)
            {
                return $"开始 {PrimaryPlan.StartAt:HH:mm}";
            }

            return PrimaryPlan.StatusText;
        }
    }

    public bool HasPrimaryPlanSubtitle => !string.IsNullOrWhiteSpace(PrimaryPlanSubtitle);
}
