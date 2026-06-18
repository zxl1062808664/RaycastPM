using RaycastPM.Models;

namespace RaycastPM.ViewModels;

public sealed class PlanCalendarEventItem
{
    public required PlanItem Plan { get; init; }

    public required string TimeText { get; init; }

    public required string StatusText { get; init; }

    public required bool IsTargetDate { get; init; }
}
