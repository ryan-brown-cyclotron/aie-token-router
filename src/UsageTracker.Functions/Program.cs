using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MudBlazor.Services;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using UsageTracker;
using UsageTracker.Functions;

var builder = FunctionsApplication.CreateBuilder(args);

// Aspire service defaults (OpenTelemetry, health, resilience) instead of direct App Insights.
builder.AddServiceDefaults();

// ASP.NET Core integration: HTTP triggers use HttpRequest / IActionResult and expose HttpContext.
var workerApp = builder.ConfigureFunctionsWebApplication();

// Host-agnostic runtime, infrastructure, and domain services.
builder.Services.AddUsageTrackerLibrary(builder.Configuration);

// Opt the Library's custom hook-ingestion instruments into this host's OTel pipeline (registered by
// AddServiceDefaults above). AddOpenTelemetry() is idempotent and .WithMetrics() accumulates, so this
// adds to - not replaces - the generic ASP.NET Core/HttpClient/runtime instrumentation.
builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(UsageTrackerMetrics.MeterName));

// Host-specific caller identity (claims in prod, dev headers locally). The isolated worker does
// not reliably populate IHttpContextAccessor, so a middleware captures HttpContext per invocation.
builder.Services.AddScoped<HttpContextHolder>();
builder.Services.AddScoped<IUserContext, FunctionsUserContext>();

// MudChart renders as static SVG, so it works under the non-interactive HtmlRenderer dashboard
// (see DashboardFunctions.cs). Registered for the components that resolve Mud services via DI;
// JS-interop-backed Mud features (dialogs, popovers, snackbars) do not work in this hosting model.
builder.Services.AddMudServices();

workerApp.UseMiddleware(async (FunctionContext context, Func<Task> next) =>
{
    var httpContext = context.GetHttpContext();
    if (httpContext is not null)
        context.InstanceServices.GetRequiredService<HttpContextHolder>().Current = httpContext;

    await next();
});

builder.Build().Run();
