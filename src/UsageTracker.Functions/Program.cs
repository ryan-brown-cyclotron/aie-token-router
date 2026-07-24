using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.JSInterop;
using MudBlazor.Services;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using UsageTracker;
using UsageTracker.Functions;
using UsageTracker.Functions.Infrastructure;

var builder = FunctionsApplication.CreateBuilder(args);

// Aspire service defaults (OpenTelemetry, health, resilience) instead of direct App Insights.
builder.AddServiceDefaults();

// ASP.NET Core integration: HTTP triggers use HttpRequest / IActionResult and expose HttpContext.
var workerApp = builder.ConfigureFunctionsWebApplication();

// Host-agnostic runtime, infrastructure, and domain services.
builder.Services.AddUsageTrackerLibrary(builder.Configuration);

// Backend forwarder for the daemon's remote compression mode: forwards to the Headroom service when
// CompressionEndpoint is configured, else falls back to the Library's local IToolOutputCompressor.
builder.Services.AddHttpClient(RemoteCompressionForwarder.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<RemoteCompressionForwarder>();

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

// Blazor Server/WASM normally supply IJSRuntime as part of their hosting model; this Functions
// host has neither, but components like MudChart still have an [Inject] IJSRuntime property that
// DI must satisfy to construct them. NoOpJsRuntime stands in - see its doc comment.
builder.Services.AddSingleton<IJSRuntime, NoOpJsRuntime>();

workerApp.UseMiddleware(async (FunctionContext context, Func<Task> next) =>
{
    var httpContext = context.GetHttpContext();
    if (httpContext is not null)
        context.InstanceServices.GetRequiredService<HttpContextHolder>().Current = httpContext;

    await next();
});

builder.Build().Run();
