using Microsoft.AspNetCore.Mvc.Filters;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Infrastructure;

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
