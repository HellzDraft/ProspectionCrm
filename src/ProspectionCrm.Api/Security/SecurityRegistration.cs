using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading.RateLimiting;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Security;

public static class SecurityRegistration
{
    public static void AddCrmSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OperatorOptions>(configuration.GetSection("Operator"));
        services.AddHttpContextAccessor();
        services.AddScoped<IHumanActor, HumanActor>();
        services.AddScoped<OperatorCookieEvents>();
        services.AddAuthentication(SecurityNames.Human)
            .AddCookie(SecurityNames.Human, options =>
            {
                options.Cookie.Name = "__Host-ProspectionCrm";
                options.Cookie.Path = "/";
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;
                options.EventsType = typeof(OperatorCookieEvents);
            })
            .AddScheme<AuthenticationSchemeOptions, IntegrationAuthenticationHandler>(SecurityNames.Machine, _ => { });
        services.AddOptions<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(SecurityNames.Human)
            .Configure<TimeProvider>((options, clock) => options.TimeProvider = clock);
        services.AddAuthorization(options =>
        {
            var human = new AuthorizationPolicyBuilder(SecurityNames.Human).RequireAuthenticatedUser()
                .RequireAssertion(c => c.User.Identity?.AuthenticationType == SecurityNames.Human).Build();
            options.DefaultPolicy = human;
            options.FallbackPolicy = human;
            options.AddPolicy(SecurityNames.Machine, new AuthorizationPolicyBuilder(SecurityNames.Machine)
                .RequireAuthenticatedUser().RequireClaim("integration-connection").Build());
        });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__Host-ProspectionCrm-CSRF";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });
        services.Configure<MvcOptions>(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
        var origins = configuration.GetSection("Operator:AllowedOrigins").Get<string[]>() ?? ["https://localhost:7075"];
        if (origins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.GetLeftPart(UriPartial.Authority) != origin || !string.IsNullOrEmpty(uri.UserInfo) || origin.Contains('*')))
            throw new InvalidOperationException("Operator:AllowedOrigins must contain exact HTTPS origins.");
        services.AddCors(options => options.AddPolicy("Blazor", policy => policy.WithOrigins(origins)
            .AllowCredentials().WithMethods("GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            .WithHeaders("Content-Type", "X-CSRF-TOKEN")));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("login", _ => RateLimitPartition.GetFixedWindowLimiter("operator", _ => new()
            { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }
}
