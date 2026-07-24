using Microsoft.JSInterop;

namespace UsageTracker.Functions.Infrastructure;

/// <summary>
/// Stand-in <see cref="IJSRuntime"/> for the non-interactive HtmlRenderer dashboard (see
/// DashboardFunctions.cs). There's no browser circuit to call into, but components like MudChart
/// have an <c>[Inject] IJSRuntime</c> property that DI must be able to satisfy just to construct
/// the component - even though it renders as static SVG and never actually invokes JS here. Any
/// interop call this stub receives is a no-op that resolves to the default value.
/// </summary>
public sealed class NoOpJsRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        ValueTask.FromResult<TValue>(default!);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        ValueTask.FromResult<TValue>(default!);
}
