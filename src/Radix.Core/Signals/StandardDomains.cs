namespace Radix.Core.Signals;

/// <summary>
/// Factory for the built-in domains. Projects store full domain definitions, so
/// these are conveniences rather than assumptions the engine depends on.
/// </summary>
public static class StandardDomains
{
    public const string BinaryId = "binary";
    public const string TernaryId = "ternary";
    public const string BalancedTernaryId = "balanced-ternary";
    public const string FourStateId = "four-state";

    /// <summary>Two discrete values labelled 0 and 1. No arithmetic semantics are implied.</summary>
    public static SignalDomain Binary() => new(
        BinaryId,
        "Binary",
        ["0", "1"],
        new SingleDriverResolution(),
        "Two-state domain. Values are indices; two's-complement and other interpretations live outside the kernel.");

    /// <summary>Three discrete values labelled 0, 1, 2 (unsaturated ternary symbols).</summary>
    public static SignalDomain Ternary() => new(
        TernaryId,
        "Ternary",
        ["0", "1", "2"],
        new SingleDriverResolution(),
        "Three-state domain. No balanced or positional interpretation is implied.");

    /// <summary>Three values labelled -, 0, + for balanced-ternary experiments.</summary>
    public static SignalDomain BalancedTernary() => new(
        BalancedTernaryId,
        "Balanced ternary",
        ["-", "0", "+"],
        new SingleDriverResolution(),
        "Three-state domain whose labels suggest balanced ternary. The kernel still treats them as ordinals.");

    /// <summary>
    /// Four-value logic domain with explicit unknown and high-impedance values.
    /// Multiple drivers follow the conventional priority X &gt; 0 = 1 &gt; Z.
    /// </summary>
    public static SignalDomain FourState() => new(
        FourStateId,
        "Four-state logic",
        ["0", "1", "X", "Z"],
        new PriorityDriverResolution([1, 1, 3, 0], conflictValue: 2),
        "Four-state domain with high impedance (Z) and unknown/conflict (X).");

    public static IReadOnlyList<SignalDomain> All() =>
        [Binary(), Ternary(), BalancedTernary(), FourState()];
}
