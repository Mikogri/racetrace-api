using Microsoft.AspNetCore.Mvc.Filters;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Infrastructure;

public sealed class AdminAuthorizationFilter : IAuthorizationFilter
{
    private readonly AdminGuard _adminGuard;

    public AdminAuthorizationFilter(AdminGuard adminGuard)
    {
        _adminGuard = adminGuard;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        _adminGuard.EnsureAuthorized(context.HttpContext.Request);
    }
}
