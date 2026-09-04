using System.Collections;

namespace Voluta.Abstractions.Channels.Reducers;

/// <summary>
///     Built-in reducer: merges string-key dictionaries.
///     Last write per key in superstep order wins; keys from different writes combine.
/// </summary>
public sealed class DictMergeReducer() : IChannelReducer
{
    /// <inheritdoc />
    public object? Reduce(object? current, IReadOnlyList<object?> writes)
    {
        if (writes.Count == 0)
        {
            return current ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        var merged = DictMergeReducerHelpers.CopyStringKeyDictionary(current);
        foreach (var write in writes)
        {
            DictMergeReducerHelpers.MergeWrite(merged, write);
        }

        return merged;
    }
}

/// <summary>
///     Dictionary copy / merge helpers for <see cref="DictMergeReducer" />.
/// </summary>
file static class DictMergeReducerHelpers
{
    public static Dictionary<string, object?> CopyStringKeyDictionary(object? current)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (current is null)
        {
            return merged;
        }

        MergeWrite(merged, current);
        return merged;
    }

    public static void MergeWrite(Dictionary<string, object?> merged, object? write)
    {
        if (write is null)
        {
            return;
        }

        if (write is IDictionary<string, object?> objectMap)
        {
            foreach (var (key, value) in objectMap)
            {
                merged[key] = value;
            }

            return;
        }

        if (write is IDictionary<string, string> stringMap)
        {
            foreach (var (key, value) in stringMap)
            {
                merged[key] = value;
            }

            return;
        }

        if (write is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key)
                {
                    throw new InvalidOperationException(
                        "DictMerge reducer requires string dictionary keys.");
                }

                merged[key] = entry.Value;
            }

            return;
        }

        throw new InvalidOperationException(
            $"DictMerge reducer requires a string-key dictionary write (got '{write.GetType().FullName}').");
    }
}
