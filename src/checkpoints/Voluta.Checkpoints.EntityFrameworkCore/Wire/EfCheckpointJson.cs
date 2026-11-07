using System.Collections;
using System.Text.Json;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Serialization;

namespace Voluta.Checkpoints.EntityFrameworkCore.Wire;

/// <summary>
///     JSON element conversion for EF checkpoint wire values (format v1 allow-list).
/// </summary>
/// <remarks>
///     Allowed shapes (wire format v1): <c>null</c>, string, bool, char, numeric primitives,
///     <see cref="Guid" />, date/time primitives, <see cref="JsonElement" />, <c>byte[]</c>,
///     lists/arrays of allowed values, and string-key dictionaries of allowed values.
///     Arbitrary CLR graphs (custom types, streams, delegates) are rejected at Put with
///     <c>checkpoint.unsupported_value_type</c>.
///     v050: with a <see cref="CheckpointTypeRegistry" /> attached, values whose exact
///     runtime type is registered stamp as <c>{"$type","$value"}</c> (primitives/strings
///     stay raw) and the allow-list learns the stamped shape; decode materializes registered
///     types back to typed instances, unknown ids fall back to the raw inner JSON.
/// </remarks>
internal static class EfCheckpointJson
{
    private const int MaxDepth = 8;
    private const string TypeKey = "$type";
    private const string ValueKey = "$value";

    public static JsonElement? ToElement(object? value, CheckpointTypeRegistry? registry = null)
    {
        return registry is not null
               && value is not null
               && !IsScalarAllowed(value)
               && registry.TryGetTypeId(value.GetType(), out var typeId)
            ? StampElement(value, typeId)
            : UnstampedElement(value);
    }

    /// <summary>
    ///     Registered runtime type: stamp. The payload is the host-approved type's
    ///     serialization (no allow-list recursion — registration is the opt-in).
    /// </summary>
    private static JsonElement? StampElement(object value, string typeId)
    {
        var stamped = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            [TypeKey] = JsonSerializer.SerializeToElement(typeId, JsonSerializerOptions.Web),
            [ValueKey] = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web),
        };
        return JsonSerializer.SerializeToElement(stamped, JsonSerializerOptions.Web);
    }

    private static JsonElement? UnstampedElement(object? value)
    {
        EnsureAllowed(value, "value", depth: 0);
        return value is null ? null : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
    }

    public static object? FromElement(JsonElement? element, CheckpointTypeRegistry? registry = null)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var json = element.Value;
        if (json.ValueKind == JsonValueKind.Object && TryReadStamped(json, out var typeId, out var value))
        {
            // Reserved stamped shape: typed restore when the registry resolves the id,
            // otherwise the raw inner JSON (forward compat with newer producers).
            return registry is not null
                && typeId.ValueKind == JsonValueKind.String
                && registry.TryResolveType(typeId.GetString()!, out var runtimeType)
                ? JsonSerializer.Deserialize(value, runtimeType, JsonSerializerOptions.Web)
                : value.Clone();
        }

        return json.ValueKind switch
        {
            JsonValueKind.String => json.GetString(),
            JsonValueKind.Number when json.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when json.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => json.EnumerateArray()
                .Select(item => FromElement(item, registry))
                .ToList(),
            _ => json.GetRawText(),
        };
    }

    public static void EnsureAllowed(object? value, string path, int depth)
    {
        EnsureAllowed(value, null, path, depth);
    }

    /// <summary>
    ///     Allow-list check; with a registry attached the reserved stamped shape
    ///     (<c>{"$type","$value"}</c> dictionary with exactly those keys) is accepted when
    ///     <c>$type</c> is a non-empty string of at most 128 chars resolving in the registry.
    /// </summary>
    public static void EnsureAllowedRegistryAware(
        object? value,
        CheckpointTypeRegistry? registry,
        string path,
        int depth)
    {
        EnsureAllowed(value, registry, path, depth);
    }

    private static void EnsureAllowed(
        object? value,
        CheckpointTypeRegistry? registry,
        string path,
        int depth)
    {
        if (value is null)
        {
            return;
        }

        if (depth > MaxDepth)
        {
            throw new CheckpointStoreException(
                CheckpointWireFormat.UnsupportedValueTypeCode,
                $"Checkpoint wire format v1 rejects value at '{path}': nesting exceeds max depth {MaxDepth}.");
        }

        if (IsScalarAllowed(value))
        {
            return;
        }

        if (value is IDictionary dictionary)
        {
            if (registry is not null && TryReadStampedDictionary(dictionary, out var typeId))
            {
                ValidateStampedTypeId(registry, typeId, path);
                EnsureAllowedRegistryAware(
                    dictionary[ValueKey],
                    registry,
                    $"{path}.{ValueKey}",
                    depth + 1);
                return;
            }

            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key)
                {
                    throw new CheckpointStoreException(
                        CheckpointWireFormat.UnsupportedValueTypeCode,
                        $"Checkpoint wire format v1 requires string dictionary keys at '{path}' (got '{entry.Key?.GetType().FullName ?? "null"}').");
                }

                EnsureAllowed(entry.Value, registry, $"{path}.{key}", depth + 1);
            }

            return;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                EnsureAllowed(item, registry, $"{path}[{index}]", depth + 1);
                index++;
            }

            return;
        }

        throw new CheckpointStoreException(
            CheckpointWireFormat.UnsupportedValueTypeCode,
            $"Checkpoint wire format v1 does not support value type '{value.GetType().FullName}' at '{path}'. "
            + "Use null, primitives, string, Guid, date/time, JsonElement, byte[], lists, or string-key dictionaries of those.");
    }

    private static bool TryReadStamped(
        JsonElement json,
        out JsonElement typeId,
        out JsonElement value)
    {
        typeId = default;
        value = default;
        if (json.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = json.EnumerateObject().ToArray();
        return properties.Length == 2
            && properties.Any(static property => property.Name == TypeKey)
            && properties.Any(static property => property.Name == ValueKey)
            && (typeId = properties.First(static property => property.Name == TypeKey).Value).ValueKind != JsonValueKind.Undefined
            && (value = properties.First(static property => property.Name == ValueKey).Value).ValueKind != JsonValueKind.Undefined;
    }

    private static bool TryReadStampedDictionary(IDictionary dictionary, out string? typeId)
    {
        typeId = null;
        if (dictionary.Contains(TypeKey) && dictionary.Contains(ValueKey) && dictionary.Count == 2)
        {
            if (dictionary[TypeKey] is string id)
            {
                typeId = id;
                return true;
            }

            // $type present but not a string: shaped like a stamp — force validation to
            // reject with the stamped-type message rather than treating it as a plain dict.
            typeId = null;
            return true;
        }

        return false;
    }

    private static void ValidateStampedTypeId(
        CheckpointTypeRegistry registry,
        string? typeId,
        string path)
    {
        if (string.IsNullOrEmpty(typeId) || typeId.Length > 128)
        {
            throw new CheckpointStoreException(
                CheckpointWireFormat.UnsupportedValueTypeCode,
                $"Checkpoint wire format v1 stamped value at '{path}' carries an invalid $type " +
                "(must be a non-empty string of at most 128 characters).");
        }

        if (!registry.TryResolveType(typeId, out _))
        {
            throw new CheckpointStoreException(
                CheckpointWireFormat.UnsupportedValueTypeCode,
                $"Checkpoint wire format v1 stamped value at '{path}' references unknown $type '{typeId}'.");
        }
    }

    private static bool IsScalarAllowed(object value)
    {
        return value is string
            or bool
            or char
            or byte
            or sbyte
            or short
            or ushort
            or int
            or uint
            or long
            or ulong
            or float
            or double
            or decimal
            or Guid
            or DateTime
            or DateTimeOffset
            or DateOnly
            or TimeOnly
            or TimeSpan
            or JsonElement
            or byte[];
    }
}
