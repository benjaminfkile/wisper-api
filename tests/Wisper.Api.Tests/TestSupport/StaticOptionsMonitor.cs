using Microsoft.Extensions.Options;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// A minimal <see cref="IOptionsMonitor{T}"/> over a fixed value — lets a unit test hand an
/// options instance to a component that reads <c>IOptionsMonitor</c> without the DI stack.
/// </summary>
public sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
