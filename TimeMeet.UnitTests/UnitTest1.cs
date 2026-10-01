using TimeMeet.Application.Meetings;
using TimeMeet.Domain.Enums;

namespace TimeMeet.UnitTests;

public sealed class MeetingWorkflowTests
{
    [Fact]
    public void NewMeetingRequest_UsesSupportedGridSteps()
    {
        var request = new CreateMeetingRequest
        {
            Title = "Созвон",
            OrganizerName = "Организатор",
            TimeZone = "Europe/Moscow",
            GridStartDate = new DateOnly(2026, 10, 1),
            GridEndDate = new DateOnly(2026, 10, 2),
            GridStepMinutes = 30,
            RetentionMode = RetentionMode.KeepForever
        };

        Assert.True(request.GridStepMinutes is 15 or 30 or 60);
        Assert.True(request.GridEndDate >= request.GridStartDate);
        Assert.Equal(RetentionMode.KeepForever, request.RetentionMode);
    }

    [Fact]
    public void AvailabilitySelection_UsesGridCellId()
    {
        var cellId = Guid.NewGuid();
        var selection = new AvailabilitySelection(cellId, AvailabilityStatus.IfNeeded);

        Assert.Equal(cellId, selection.GridCellId);
        Assert.Equal(AvailabilityStatus.IfNeeded, selection.Status);
    }

    [Fact]
    public void AvailabilityStatus_ContainsOnlySupportedValues()
    {
        Assert.Equal(0, (int)AvailabilityStatus.Available);
        Assert.Equal(1, (int)AvailabilityStatus.IfNeeded);
        Assert.Equal(2, Enum.GetValues<AvailabilityStatus>().Length);
    }

    [Fact]
    public void MeetingStatus_ContainsLifecycleStatuses()
    {
        Assert.Contains(MeetingStatus.Active, Enum.GetValues<MeetingStatus>());
        Assert.Contains(MeetingStatus.Closed, Enum.GetValues<MeetingStatus>());
        Assert.Contains(MeetingStatus.Archived, Enum.GetValues<MeetingStatus>());
        Assert.Contains(MeetingStatus.Deleted, Enum.GetValues<MeetingStatus>());
    }
}
