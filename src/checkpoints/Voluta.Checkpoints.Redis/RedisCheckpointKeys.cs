namespace Voluta.Checkpoints.Redis;

/// <summary>
///     Key helpers for the Redis checkpoint layout.
/// </summary>
internal static class RedisCheckpointKeys
{
    private static readonly char[] InvalidKeyChars =
    [
        '{', '}', ':', '\\', '%', '*', '?', '[', ']', '(', ')',
        '\0', '\r', '\n', '\t', ' ',
    ];

    public static string SanitizeThreadId(string threadId)
    {
        var safe = threadId;
        foreach (var ch in InvalidKeyChars)
        {
            safe = safe.Replace(ch, '_');
        }

        return safe;
    }

    /// <summary>Sorted-set key holding one thread's checkpoints: <c>{prefix}thread:{safeThreadId}</c>.</summary>
    public static string ThreadKey(string? keyPrefix, string threadId)
    {
        var prefix = NormalizePrefix(keyPrefix);
        return prefix + "thread:" + SanitizeThreadId(threadId);
    }

    /// <summary>
    ///     Key of the per-thread head marker (guarded-put CAS state): <c>{prefix}head:{safeThreadId}</c>.
    ///     Deliberately outside the <c>thread:</c> namespace so thread discovery
    ///     (<see cref="ThreadScanPattern" /> / <see cref="TryParseThreadIdFromKey" />) never
    ///     surfaces it as a thread.
    /// </summary>
    public static string HeadKey(string? keyPrefix, string threadId)
    {
        var prefix = NormalizePrefix(keyPrefix);
        return prefix + "head:" + SanitizeThreadId(threadId);
    }

    /// <summary>Scan pattern matching every thread key under the prefix.</summary>
    public static string ThreadScanPattern(string? keyPrefix)
    {
        return NormalizePrefix(keyPrefix) + "thread:*";
    }

    /// <summary>Extracts a thread id from a scanned key, or <c>null</c> when the shape does not match.</summary>
    public static string? TryParseThreadIdFromKey(string key, string? keyPrefix)
    {
        var prefix = NormalizePrefix(keyPrefix) + "thread:";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var threadId = key[prefix.Length..];
        return string.IsNullOrEmpty(threadId) ? null : threadId;
    }

    private static string NormalizePrefix(string? keyPrefix)
    {
        if (string.IsNullOrWhiteSpace(keyPrefix))
        {
            return string.Empty;
        }

        var trimmed = keyPrefix.Trim().TrimEnd(':');
        return string.IsNullOrEmpty(trimmed) ? string.Empty : trimmed + ":";
    }
}
