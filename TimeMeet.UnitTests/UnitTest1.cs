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
            GridStepMinutes = 30
        };

        Assert.True(request.GridStepMinutes is 15 or 30 or 60);
        Assert.True(request.GridEndDate >= request.GridStartDate);
    }

    [Fact]
    public void AvailabilitySelection_UsesGridCellTargetId()
    {
        var targetId = Guid.NewGuid();
        var selection = new AvailabilitySelection(targetId, AvailabilityStatus.IfNeeded);

        Assert.Equal(targetId, selection.TargetId);
        Assert.Equal(AvailabilityStatus.IfNeeded, selection.Status);
    }

    [Fact]
    public void AvailabilityStatus_ContainsOnlySupportedValues()
    {
        Assert.True(Enum.IsDefined(typeof(AvailabilityStatus), AvailabilityStatus.Available));
        Assert.True(Enum.IsDefined(typeof(AvailabilityStatus), AvailabilityStatus.IfNeeded));
        Assert.Equal(2, Enum.GetValues<AvailabilityStatus>().Length);
    }

    [Fact]
    public void OrganizerManagementPath_UsesMeetingCode()
    {
        var path = MeetingNavigation.GetManagementPath("abc 123");

        Assert.Equal("/manage/abc%20123", path);
    }
}
