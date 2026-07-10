using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Azure.Functions.Worker;

namespace UsageTracker.Functions;

/// <summary>
/// Serves static files (dashboard logos, etc.) from <c>wwwroot</c>. The isolated worker has no
/// generic ASP.NET Core middleware pipeline - <c>UseStaticFiles()</c> isn't available on the
/// builder returned by <c>ConfigureFunctionsWebApplication()</c> - so files are served through an
/// ordinary HTTP-triggered function instead, the same as every other route in this app.
/// </summary>
public sealed class AssetFunctions
{
    private static readonly string WebRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    [Function("Assets")]
    public IActionResult GetAsset(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "assets/{*path}")] HttpRequest req,
        string path)
    {
        var fullPath = Path.GetFullPath(Path.Combine(WebRoot, path ?? string.Empty));

        // Guard against path traversal (e.g. "../../secrets.txt") escaping wwwroot.
        if (!fullPath.StartsWith(WebRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            return new NotFoundResult();

        if (!ContentTypeProvider.TryGetContentType(fullPath, out var contentType))
            contentType = "application/octet-stream";

        return new FileStreamResult(File.OpenRead(fullPath), contentType);
    }
}
