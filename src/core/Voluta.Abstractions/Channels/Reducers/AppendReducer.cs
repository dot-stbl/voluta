using System.Collections;

namespace Voluta.Abstractions.Channels.Reducers;

/// <summary>
///     Built-in reducer: concatenates writes into an ordered list.
///     Enumerable writes are flattened except <see cref="string" />.
/// </summary>
public sealed class AppendReducer() : IChannelReducer
{
    /// <inheritdoc />
    public object? Reduce(object? current, IReadOnlyList<object?> writes)
    {
        if (writes.Count == 0)
        {
            return current ?? new List<object?>();
        }

        var items = AppendReducerHelpers.MaterializeList(current);
        foreach (var write in writes)
        {
            AppendReducerHelpers.AppendValue(items, write);
        }

        return items;
    }
}

/// <summary>
///     Flatten / materialize helpers for <see cref="AppendReducer" />.
/// </summary>
file static class AppendReducerHelpers
{
    public static void AppendValue(List<object?> items, object? write)
    {
        if (write is null)
        {
            items.Add(null);
            return;
        }

        if (write is string)
        {
            items.Add(write);
            return;
        }

        if (write is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                items.Add(item);
            }

            return;
        }

        items.Add(write);
    }

    public static List<object?> MaterializeList(object? restored)
    {
        if (restored is null)
        {
            return [];
        }

        if (restored is List<object?> list)
        {
            return [.. list];
        }

        if (restored is IEnumerable enumerable and not string)
        {
            var items = new List<object?>();
            foreach (var item in enumerable)
            {
                items.Add(item);
            }

            return items;
        }

        return [restored];
    }
}
