using Hangfire.Dashboard;

namespace TimeMeet.Web;

public sealed class LocalRequestsOnlyDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.Connection.RemoteIpAddress is not null
            && (System.Net.IPAddress.IsLoopback(httpContext.Connection.RemoteIpAddress)
                || httpContext.Connection.RemoteIpAddress.Equals(httpContext.Connection.LocalIpAddress));
    }
}