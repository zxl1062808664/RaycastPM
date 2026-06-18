using RaycastPM.Models;

namespace RaycastPM.ViewModels;

public sealed class PlanCalendarDetailItem : ObservableObject
{
    private bool _isSelected;

    public required PlanItem Plan { get; init; }

    public required string DateText { get; init; }

    public required string TimeText { get; init; }

    public required string DurationText { get; init; }

    public required string StatusText { get; init; }

    public required string PriorityText { get; init; }

    public required string SummaryText { get; init; }

    public required string TagText { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
