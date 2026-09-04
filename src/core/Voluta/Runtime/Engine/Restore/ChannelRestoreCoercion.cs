using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Voluta.Abstractions.Channels;
using Voluta.Exceptions.Run;

namespace Voluta.Runtime.Engine.Restore;

/// <summary>
///     Coerces checkpoint wire values to the channel's declared CLR type.
/// </summary>
internal static class ChannelRestoreCoercion
{
    public static object? Coerce(string channelName, object? value, Type? valueType, ChannelKind kind)
    {
        return ChannelRestoreCoercionHelpers.Coerce(channelName, value, valueType, kind);
    }
}

/// <summary>
///     Type conversion helpers for <see cref="ChannelRestoreCoercion" />.
/// </summary>
file static class ChannelRestoreCoercionHelpers
{
    public static object? Coerce(string channelName, object? value, Type? valueType, ChannelKind kind)
    {
        if (valueType is null)
        {
            return value;
        }

        try
        {
            return kind == ChannelKind.Append
                ? CoerceAppendList(value, AppendElementType(valueType))
                : CoerceOne(value, valueType);
        }
        catch (GraphRunFailedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GraphRunFailedException(
                $"Channel '{channelName}' could not restore value as '{valueType.FullName}'.",
                exception);
        }
    }

    public static Type AppendElementType(Type valueType)
    {
        var targetType = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (targetType == typeof(string) || targetType == typeof(object))
        {
            return targetType;
        }

        if (targetType.IsGenericType && targetType.GetGenericArguments() is { Length: 1 } arguments)
        {
            var definition = targetType.GetGenericTypeDefinition();
            if (definition == typeof(IEnumerable<>)
                || definition == typeof(IReadOnlyList<>)
                || definition == typeof(IReadOnlyCollection<>)
                || definition == typeof(IList<>)
                || definition == typeof(List<>)
                || definition == typeof(ICollection<>))
            {
                return arguments[0];
            }
        }

        return targetType;
    }

    public static object? CoerceAppendList(object? value, Type elementType)
    {
        var items = new List<object?>();
        if (value is null)
        {
            return items;
        }

        if (value is string or not IEnumerable)
        {
            items.Add(CoerceOne(value, elementType));
            return items;
        }

        foreach (var item in (IEnumerable)value)
        {
            items.Add(CoerceOne(item, elementType));
        }

        return items;
    }

    public static object? CoerceOne(object? value, Type valueType)
    {
        if (value is null)
        {
            return null;
        }

        var targetType = Nullable.GetUnderlyingType(valueType) ?? valueType;
        return targetType == typeof(object) || targetType.IsInstanceOfType(value)
            ? value
            : TryConvertNumber(value, targetType, out var convertedNumber)
                ? convertedNumber
                : value switch
                {
                    JsonElement jsonElement => Deserialize(jsonElement, targetType),
                    string text when LooksLikeJson(text) && targetType != typeof(string) =>
                        Deserialize(text, targetType),
                    IDictionary => Deserialize(SerializeToElement(value), targetType),
                    IEnumerable and not string => Deserialize(SerializeToElement(value), targetType),
                    IConvertible convertible => Convert.ChangeType(
                        convertible,
                        targetType,
                        CultureInfo.InvariantCulture),
                    _ => throw new InvalidCastException(
                        $"Cannot convert '{value.GetType().FullName}' to '{targetType.FullName}'."),
                };
    }

    public static bool LooksLikeJson(string text)
    {
        var span = text.AsSpan().Trim();
        return span.Length > 0 && span[0] is '{' or '[';
    }

    public static bool TryConvertNumber(object value, Type targetType, [NotNullWhen(true)] out object? converted)
    {
        converted = null;
        if (!IsNumeric(targetType) || value is not IConvertible)
        {
            return false;
        }

        converted = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        return converted is not null;
    }

    public static bool IsNumeric(Type type)
    {
        return type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong)
            || type == typeof(float)
            || type == typeof(double)
            || type == typeof(decimal);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Restore type is declared on the compiled graph; wire values are checkpoint allow-list shapes.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Restore type is declared on the compiled graph; wire values are checkpoint allow-list shapes.")]
    public static JsonElement SerializeToElement(object value)
    {
        return JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Restore type is declared on the compiled graph; wire values are checkpoint allow-list shapes.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Restore type is declared on the compiled graph; wire values are checkpoint allow-list shapes.")]
    public static object? Deserialize(JsonElement element, Type targetType)
    {
        return JsonSerializer.Deserialize(element, targetType, JsonSerializerOptions.Web);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Restore type is declared on the compiled graph; wire values are checkpoint allow-list shapes.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Restore type is declared on the compiled graph; wire values are checkpoint allow-list shapes.")]
    public static object? Deserialize(string json, Type targetType)
    {
        return JsonSerializer.Deserialize(json, targetType, JsonSerializerOptions.Web);
    }
}
