namespace Voluta.Graph.Options;

/// <summary>
///     Sentinel for "no superstep limit"; does not collide with
///     <see cref="CompileOptions.RecursionLimit" /> semantics (25 default stays shipped).
/// </summary>
public static class RecursionLimit
{
    /// <summary>
    ///     Pass to <see cref="CompileOptions" /> (or use
    ///     <see cref="CompileOptions.WithoutRecursionLimit" />) to remove the superstep cap;
    ///     stop with <see cref="CompileOptions.RunGuards" /> or terminal nodes instead.
    /// </summary>
    public const int Unlimited = int.MaxValue;
}
