using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var cosmos = builder.AddAzureCosmosDB("cosmos");

if (builder.Environment.IsDevelopment())
{
	cosmos.RunAsEmulator();
}

var usageDatabase = cosmos.AddCosmosDatabase("usage-tracker");

// Pinned to 7071 (the func default) so local hook configs (.claude/settings.json,
// copilot.hooks.json, etc.) always find this endpoint without editing them per Aspire run -
// otherwise Aspire assigns a random port each time the AppHost starts.
// For non-proxy endpoints (Functions runs in-process), port and targetPort must match.
// The read-only admin dashboard (GET /api/dashboard) is rendered server-side inside this same
// Function App via HtmlRenderer - see UsageTracker.Functions/Functions/DashboardFunctions.cs - so
// there's no separate Dashboard project/resource to add here.
var functions = builder.AddAzureFunctionsProject<Projects.UsageTracker_Functions>("usage-tracker-functions")
	.WithHttpEndpoint(port: 7071, targetPort: 7071, name: "http")
	.WithReference(usageDatabase);

builder.Build().Run();
