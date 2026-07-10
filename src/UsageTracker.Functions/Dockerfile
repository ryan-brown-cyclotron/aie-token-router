# Containerized Azure Functions (isolated worker, .NET 8) for Azure Container Apps.
# Build context is the repo root so referenced projects (Library, ServiceDefaults) are available.
FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0 AS base
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo
COPY . .
RUN dotnet publish src/UsageTracker.Functions/UsageTracker.Functions.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
COPY --from=build /app/publish /home/site/wwwroot
