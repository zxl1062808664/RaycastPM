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

    /// <summary>
    /// 状态分类标识，用于在 UI 上为状态标签着色。
    /// 取值：Overdue / Completed / Blocked / InProgress。
    /// </summary>
    public required string StatusKind { get; init; }

    /// <summary>
    /// 计划原始状态标识，用于为“状态”徽章着色。
    /// 取值同 PlanItemStatus 枚举名：NotStarted / InProgress / Completed / Blocked。
    /// </summary>
    public required string PlanStatusKind { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
