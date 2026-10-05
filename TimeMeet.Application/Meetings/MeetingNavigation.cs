namespace TimeMeet.Application.Meetings;

public static class MeetingNavigation
{
    public static string GetManagementPath(string shortCode) =>
        $"/manage/{Uri.EscapeDataString(shortCode)}";
}