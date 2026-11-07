namespace Voluta.Checkpoints.S3.Wire;

/// <summary>
///     Object-key helpers for S3 checkpoint layout.
/// </summary>
internal static class S3CheckpointKeys
{
    private static readonly char[] InvalidKeyChars =
    [
        '\\', '{', '}', '^', '%', '`', ']', '"', '>', '[', '~', '<', '#', '|',
        '\0', '\r', '\n', '\t',
    ];

    public static string SanitizeThreadId(string threadId)
    {
        var safe = threadId;
        foreach (var ch in InvalidKeyChars)
        {
            safe = safe.Replace(ch, '_');
        }

        return safe.Replace(' ', '_');
    }

    public static string ThreadPrefix(string? keyPrefix, string threadId)
    {
        var safeThread = SanitizeThreadId(threadId);
        if (string.IsNullOrWhiteSpace(keyPrefix))
        {
            return safeThread + "/";
        }

        var trimmed = keyPrefix.Trim().Trim('/');
        return string.IsNullOrEmpty(trimmed) ? safeThread + "/" : trimmed + "/" + safeThread + "/";
    }

    public static string ObjectKey(string? keyPrefix, string threadId, long step)
    {
        return ThreadPrefix(keyPrefix, threadId) + $"{step:D12}.json";
    }

    /// <summary>
    ///     Key of the per-thread latest-step pointer document used by guarded puts.
    ///     The stem is not numeric, so step-document enumeration (<see cref="TryParseStep" />)
    ///     ignores it.
    /// </summary>
    public static string PointerKey(string? keyPrefix, string threadId)
    {
        return ThreadPrefix(keyPrefix, threadId) + "latest.json";
    }

    /// <summary>
    ///     Prefix used when listing all threads (trailing slash when a key prefix is set).
    /// </summary>
    public static string RootPrefix(string? keyPrefix)
    {
        if (string.IsNullOrWhiteSpace(keyPrefix))
        {
            return string.Empty;
        }

        var trimmed = keyPrefix.Trim().Trim('/');
        return string.IsNullOrEmpty(trimmed) ? string.Empty : trimmed + "/";
    }

    /// <summary>
    ///     Extracts a thread id segment from a ListObjects CommonPrefix under <paramref name="rootPrefix" />.
    /// </summary>
    public static bool TryParseThreadIdFromCommonPrefix(
        string commonPrefix,
        string rootPrefix,
        out string threadId)
    {
        threadId = string.Empty;
        if (string.IsNullOrEmpty(commonPrefix))
        {
            return false;
        }

        var relative = commonPrefix;
        if (!string.IsNullOrEmpty(rootPrefix))
        {
            if (!commonPrefix.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            relative = commonPrefix[rootPrefix.Length..];
        }

        relative = relative.Trim('/');
        if (string.IsNullOrEmpty(relative) || relative.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        threadId = relative;
        return true;
    }

    public static bool TryParseStep(string objectKey, out long step)
    {
        step = 0;
        var fileName = objectKey;
        var slash = objectKey.LastIndexOf('/');
        if (slash >= 0 && slash < objectKey.Length - 1)
        {
            fileName = objectKey[(slash + 1)..];
        }

        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = fileName[..^".json".Length];
        return long.TryParse(stem, out step);
    }
}
