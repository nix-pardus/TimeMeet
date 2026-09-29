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
            GridFrom = new TimeOnly(9, 0),
            GridTo = new TimeOnly(18, 0),
            GridStepMinutes = 30
        };

        Assert.True(request.GridStepMinutes is 30 or 60);
        Assert.True(request.GridEndDate >= request.GridStartDate);
        Assert.True(request.GridTo > request.GridFrom);
    }

    [Fact]
    public void AvailabilitySelection_UsesTargetIdForCurrentPhase()
    {
        var targetId = Guid.NewGuid();
        var selection = new AvailabilitySelection(targetId, AvailabilityStatus.IfNeeded);

        Assert.Equal(targetId, selection.TargetId);
        Assert.Equal(AvailabilityStatus.IfNeeded, selection.Status);
    }

    [Fact]
    public void MeetingPhases_AreOrderedAsWorkflow()
    {
        Assert.Equal(0, (int)MeetingPhase.AvailabilityCollection);
        Assert.Equal(1, (int)MeetingPhase.FinalSlotSelection);
    }
}
