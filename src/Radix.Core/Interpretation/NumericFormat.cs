using System.Globalization;
using System.Numerics;

namespace Radix.Core.Interpretation;

/// <summary>Result of interpreting a signal sequence as a number.</summary>
public readonly record struct NumericDecodeResult(bool IsValid, BigInteger Value, string? Error)
{
    public static NumericDecodeResult Valid(BigInteger value) => new(true, value, null);

    public static NumericDecodeResult Invalid(string error) => new(false, BigInteger.Zero, error);

    public string Display() => IsValid
        ? Value.ToString(CultureInfo.InvariantCulture)
        : $"invalid: {Error}";
}

/// <summary>
/// Display-side number interpretation. Implementations are pure functions over
/// signal-value sequences and never participate in simulation.
/// </summary>
public interface INumericFormat
{
    /// <summary>Stable identifier.</summary>
    public string Id { get; }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Domain this format is designed for (informational).</summary>
    public string DomainId { get; }

    /// <summary>Documentation of conventions and ranges.</summary>
    public string? Description { get; }

    /// <summary>Whether the first signal in the span is the least significant.</summary>
    public bool LeastSignificantFirst { get; }

    /// <summary>Decodes a sequence of signal value indices.</summary>
    public NumericDecodeResult Decode(ReadOnlySpan<int> signalValues);

    /// <summary>
    /// Encodes a number as signal value indices in the same orientation this format
    /// decodes. <paramref name="signalValues"/> determines the width. Encoding is a
    /// display/input convenience; callers still drive independent pins afterward.
    /// </summary>
    public bool TryEncode(BigInteger value, Span<int> signalValues, out string? error);

    public BigInteger MinimumValue(int width);

    public BigInteger MaximumValue(int width);

    public string Describe(int width);
}

/// <summary>How the most significant position is weighted.</summary>
public enum WeightingMode
{
    /// <summary>Every position contributes digit × radix^position.</summary>
    Standard,

    /// <summary>
    /// The most significant position contributes digit × −radix^(width−1).
    /// For radix 2 this is two's complement. For other radices it is a documented
    /// radix-complement weight convention, not necessarily a symmetric range.
    /// </summary>
    RadixComplement,

    /// <summary>
    /// The most significant position contributes digit × −(radix^(width−1) − 1).
    /// For radix 2 this is ones' complement; it is intended for binary use.
    /// </summary>
    DiminishedRadixComplement,
}

/// <summary>
/// Positional interpretation with an arbitrary digit set and radix. Signal indices
/// are mapped to digit values by position in <see cref="DigitValues"/>.
/// </summary>
public sealed class PositionalFormat : INumericFormat
{
    public PositionalFormat(
        string id,
        string name,
        string domainId,
        IEnumerable<long> digitValues,
        long radix,
        WeightingMode weighting = WeightingMode.Standard,
        bool leastSignificantFirst = true,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(domainId);
        ArgumentNullException.ThrowIfNull(digitValues);

        DigitValues = digitValues.ToArray();
        if (DigitValues.Count == 0)
        {
            throw new ArgumentException("At least one digit value is required.", nameof(digitValues));
        }

        if (DigitValues.Distinct().Count() != DigitValues.Count)
        {
            throw new ArgumentException("Digit values must be distinct.", nameof(digitValues));
        }

        if (radix < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(radix), "Radix must be at least 2.");
        }

        Id = id;
        Name = name;
        DomainId = domainId;
        Radix = radix;
        Weighting = weighting;
        LeastSignificantFirst = leastSignificantFirst;
        Description = description;
    }

    public string Id { get; }

    public string Name { get; }

    public string DomainId { get; }

    public string? Description { get; }

    public long Radix { get; }

    public IReadOnlyList<long> DigitValues { get; }

    public WeightingMode Weighting { get; }

    public bool LeastSignificantFirst { get; }

    public NumericDecodeResult Decode(ReadOnlySpan<int> signalValues)
    {
        BigInteger sum = BigInteger.Zero;
        for (int position = 0; position < signalValues.Length; position++)
        {
            int signal = SignalAt(signalValues, position);
            if (signal < 0)
            {
                return NumericDecodeResult.Invalid("sequence contains an undriven signal");
            }

            if (signal >= DigitValues.Count)
            {
                return NumericDecodeResult.Invalid(
                    $"signal value {signal.ToString(CultureInfo.InvariantCulture)} has no digit mapping");
            }

            sum += DigitValues[signal] * Weight(position, signalValues.Length);
        }

        return NumericDecodeResult.Valid(sum);
    }

    public bool TryEncode(BigInteger value, Span<int> signalValues, out string? error)
    {
        signalValues.Clear();
        int width = signalValues.Length;
        BigInteger minimum = MinimumValue(width);
        BigInteger maximum = MaximumValue(width);
        if (value < minimum || value > maximum)
        {
            error = $"value {value} is outside {Name} width {width} range {minimum}..{maximum}";
            return false;
        }

        if (width == 0)
        {
            error = null;
            return true;
        }

        long minDigit = DigitValues.Min();
        long maxDigit = DigitValues.Max();
        if (maxDigit - minDigit + 1 != DigitValues.Count)
        {
            error = $"{Name} encoding requires a contiguous digit set";
            return false;
        }

        BigInteger radix = Radix;
        BigInteger lowerWeight = BigInteger.One;
        BigInteger lowerSum = BigInteger.Zero;
        for (int position = 0; position < width - 1; position++)
        {
            lowerSum += lowerWeight;
            lowerWeight *= radix;
        }

        if (Weighting == WeightingMode.Standard)
        {
            BigInteger offset = value - (minDigit * (lowerSum + lowerWeight));
            for (int position = 0; position < width; position++)
            {
                offset = BigInteger.DivRem(offset, radix, out BigInteger remainder);
                int signal = LeastSignificantFirst ? position : width - 1 - position;
                if (!TryDigitIndex(minDigit + (long)remainder, out signalValues[signal]))
                {
                    error = $"{Name} cannot represent digit {minDigit + (long)remainder}";
                    return false;
                }
            }

            if (!offset.IsZero)
            {
                error = $"value {value} does not fit {Name} width {width}";
                return false;
            }

            error = null;
            return true;
        }

        BigInteger topWeight = Weighting == WeightingMode.RadixComplement
            ? -lowerWeight
            : -(lowerWeight - 1);
        BigInteger topSpan = -topWeight;
        BigInteger offsetValue = value - (minDigit * (lowerSum + topWeight));
        BigInteger topOffset = offsetValue >= 0 || topSpan.IsZero
            ? BigInteger.Zero
            : CeilingDivide(-offsetValue, topSpan);
        BigInteger lowerValue = offsetValue + (topSpan * topOffset);
        if (topOffset < 0 ||
            topOffset >= radix ||
            lowerValue < 0 ||
            lowerValue > lowerWeight - 1)
        {
            error = $"value {value} does not fit {Name} width {width}";
            return false;
        }

        for (int position = 0; position < width - 1; position++)
        {
            lowerValue = BigInteger.DivRem(lowerValue, radix, out BigInteger remainder);
            int signal = LeastSignificantFirst ? position : width - 1 - position;
            if (!TryDigitIndex(minDigit + (long)remainder, out signalValues[signal]))
            {
                error = $"{Name} cannot represent digit {minDigit + (long)remainder}";
                return false;
            }
        }

        int topSignal = LeastSignificantFirst ? width - 1 : 0;
        if (!TryDigitIndex(minDigit + (long)topOffset, out signalValues[topSignal]))
        {
            error = $"{Name} cannot represent digit {minDigit + (long)topOffset}";
            return false;
        }

        error = null;
        return true;
    }

    public BigInteger MinimumValue(int width) => Extreme(width, maximum: false);

    public BigInteger MaximumValue(int width) => Extreme(width, maximum: true);

    public string Describe(int width)
    {
        string digits = string.Join(",", DigitValues.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Name} (radix {Radix}, digits {digits}, width {width}, range {MinimumValue(width)}..{MaximumValue(width)})");
    }

    private bool TryDigitIndex(long digit, out int index)
    {
        for (int i = 0; i < DigitValues.Count; i++)
        {
            if (DigitValues[i] == digit)
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private static BigInteger CeilingDivide(BigInteger numerator, BigInteger denominator) =>
        (numerator + denominator - 1) / denominator;

    private int SignalAt(ReadOnlySpan<int> values, int significance) =>
        LeastSignificantFirst ? values[significance] : values[values.Length - 1 - significance];

    private BigInteger Weight(int position, int width)
    {
        BigInteger standard = BigInteger.Pow(Radix, position);
        if (Weighting == WeightingMode.Standard || position != width - 1)
        {
            return standard;
        }

        return Weighting switch
        {
            WeightingMode.RadixComplement => -standard,
            WeightingMode.DiminishedRadixComplement => -(standard - 1),
            _ => standard,
        };
    }

    private BigInteger Extreme(int width, bool maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        long minDigit = DigitValues.Min();
        long maxDigit = DigitValues.Max();
        BigInteger sum = BigInteger.Zero;
        for (int position = 0; position < width; position++)
        {
            BigInteger weight = Weight(position, width);
            long digit = weight.Sign >= 0
                ? (maximum ? maxDigit : minDigit)
                : (maximum ? minDigit : maxDigit);
            sum += digit * weight;
        }

        return sum;
    }
}

/// <summary>
/// Sign-and-magnitude interpretation. The most significant signal is a sign indicator;
/// all remaining positions are an unsigned magnitude in the given radix.
/// </summary>
public sealed class SignMagnitudeFormat : INumericFormat
{
    public SignMagnitudeFormat(
        string id,
        string name,
        string domainId,
        IEnumerable<long> digitValues,
        long radix,
        int negativeSignValue,
        bool leastSignificantFirst = true,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(domainId);
        ArgumentNullException.ThrowIfNull(digitValues);

        DigitValues = digitValues.ToArray();
        if (DigitValues.Count == 0 || DigitValues.Any(v => v < 0))
        {
            throw new ArgumentException("Sign-magnitude digits must be non-negative.", nameof(digitValues));
        }

        if (radix < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(radix));
        }

        if (negativeSignValue < 0 || negativeSignValue >= DigitValues.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(negativeSignValue));
        }

        Id = id;
        Name = name;
        DomainId = domainId;
        Radix = radix;
        NegativeSignValue = negativeSignValue;
        LeastSignificantFirst = leastSignificantFirst;
        Description = description;
    }

    public string Id { get; }

    public string Name { get; }

    public string DomainId { get; }

    public string? Description { get; }

    public long Radix { get; }

    public IReadOnlyList<long> DigitValues { get; }

    public int NegativeSignValue { get; }

    public bool LeastSignificantFirst { get; }

    public NumericDecodeResult Decode(ReadOnlySpan<int> signalValues)
    {
        if (signalValues.IsEmpty)
        {
            return NumericDecodeResult.Valid(BigInteger.Zero);
        }

        int signSignal = LeastSignificantFirst ? signalValues[^1] : signalValues[0];
        if (signSignal < 0 || signSignal >= DigitValues.Count)
        {
            return NumericDecodeResult.Invalid("sign signal has no digit mapping");
        }

        BigInteger magnitude = BigInteger.Zero;
        for (int position = 0; position < signalValues.Length - 1; position++)
        {
            int signal = LeastSignificantFirst
                ? signalValues[position]
                : signalValues[signalValues.Length - 2 - position];
            if (signal < 0)
            {
                return NumericDecodeResult.Invalid("sequence contains an undriven signal");
            }

            if (signal >= DigitValues.Count)
            {
                return NumericDecodeResult.Invalid($"signal value {signal} has no digit mapping");
            }

            magnitude += DigitValues[signal] * BigInteger.Pow(Radix, position);
        }

        bool negative = signSignal == NegativeSignValue;
        return NumericDecodeResult.Valid(negative ? BigInteger.Negate(magnitude) : magnitude);
    }

    public bool TryEncode(BigInteger value, Span<int> signalValues, out string? error)
    {
        signalValues.Clear();
        int width = signalValues.Length;
        BigInteger minimum = MinimumValue(width);
        BigInteger maximum = MaximumValue(width);
        if (value < minimum || value > maximum)
        {
            error = $"value {value} is outside {Name} width {width} range {minimum}..{maximum}";
            return false;
        }

        if (width == 0)
        {
            error = null;
            return true;
        }

        int zeroIndex = IndexOfDigit(0);
        if (zeroIndex < 0)
        {
            error = $"{Name} requires digit 0";
            return false;
        }

        for (int i = 0; i < signalValues.Length; i++)
        {
            signalValues[i] = zeroIndex;
        }

        if (width == 1)
        {
            error = null;
            return true;
        }

        BigInteger magnitude = BigInteger.Abs(value);
        int magnitudeCount = width - 1;
        for (int position = 0; position < magnitudeCount; position++)
        {
            magnitude = BigInteger.DivRem(magnitude, Radix, out BigInteger remainder);
            int index = IndexOfDigit((long)remainder);
            if (index < 0)
            {
                error = $"{Name} cannot represent magnitude digit {remainder}";
                return false;
            }

            int signal = LeastSignificantFirst ? position : width - 2 - position;
            signalValues[signal] = index;
        }

        if (!magnitude.IsZero)
        {
            error = $"value {value} does not fit {Name} width {width}";
            return false;
        }

        int signPosition = LeastSignificantFirst ? width - 1 : 0;
        signalValues[signPosition] = value.Sign < 0
            ? NegativeSignValue
            : (NegativeSignValue == 0 ? PreferredPositiveSign() : 0);
        error = null;
        return true;
    }

    private int IndexOfDigit(long digit)
    {
        for (int i = 0; i < DigitValues.Count; i++)
        {
            if (DigitValues[i] == digit)
            {
                return i;
            }
        }

        return -1;
    }

    private int PreferredPositiveSign()
    {
        for (int i = 0; i < DigitValues.Count; i++)
        {
            if (i != NegativeSignValue)
            {
                return i;
            }
        }

        return 0;
    }

    public BigInteger MinimumValue(int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        return width <= 1 ? BigInteger.Zero : BigInteger.Negate(MaxMagnitude(width));
    }

    public BigInteger MaximumValue(int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        return width <= 1 ? BigInteger.Zero : MaxMagnitude(width);
    }

    public string Describe(int width) => string.Create(
        CultureInfo.InvariantCulture,
        $"{Name} (radix {Radix}, width {width}, range {MinimumValue(width)}..{MaximumValue(width)}, negative zero exists)");

    private BigInteger MaxMagnitude(int width)
    {
        BigInteger sum = BigInteger.Zero;
        long maxDigit = DigitValues.Max();
        for (int position = 0; position < width - 1; position++)
        {
            sum += maxDigit * BigInteger.Pow(Radix, position);
        }

        return sum;
    }
}

/// <summary>
/// A project-owned numeric format descriptor. It is display metadata, not a signal
/// domain or simulation component: removing every format would leave simulation
/// behavior unchanged. Custom formats let a project use digit mappings and radices
/// that are not in the built-in catalog.
/// </summary>
public sealed class NumericFormatDefinition
{
    public NumericFormatDefinition(
        string id,
        string name,
        string domainId,
        IEnumerable<long> digitValues,
        long radix,
        WeightingMode weighting = WeightingMode.Standard,
        bool leastSignificantFirst = true,
        int? negativeSignValue = null,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(domainId);
        ArgumentNullException.ThrowIfNull(digitValues);

        Id = id;
        Name = name;
        DomainId = domainId;
        DigitValues = digitValues.ToArray();
        Radix = radix;
        Weighting = weighting;
        LeastSignificantFirst = leastSignificantFirst;
        NegativeSignValue = negativeSignValue;
        Description = description;

        // Reuse the interpretation constructors so project formats cannot smuggle in
        // digit, radix, or sign-index combinations that the decoder would reject.
        _ = ToFormat();
    }

    public string Id { get; }

    public string Name { get; }

    public string DomainId { get; }

    public IReadOnlyList<long> DigitValues { get; }

    public long Radix { get; }

    public WeightingMode Weighting { get; }

    public bool LeastSignificantFirst { get; }

    public int? NegativeSignValue { get; }

    public string? Description { get; }

    public bool IsSignMagnitude => NegativeSignValue is not null;

    public INumericFormat ToFormat() => NegativeSignValue is int sign
        ? new SignMagnitudeFormat(
            Id,
            Name,
            DomainId,
            DigitValues,
            Radix,
            sign,
            LeastSignificantFirst,
            Description)
        : new PositionalFormat(
            Id,
            Name,
            DomainId,
            DigitValues,
            Radix,
            Weighting,
            LeastSignificantFirst,
            Description);

    public override string ToString() => Id;
}

/// <summary>Ready-made formats. Users can construct additional formats freely.</summary>
public static class NumericFormats
{
    public static PositionalFormat UnsignedBinary { get; } = new(
        "unsigned-binary",
        "Unsigned binary",
        Signals.StandardDomains.BinaryId,
        [0, 1],
        2,
        description: "Σ bit_i × 2^i; range 0..2^n−1.");

    public static PositionalFormat TwosComplement { get; } = new(
        "twos-complement",
        "Two's complement",
        Signals.StandardDomains.BinaryId,
        [0, 1],
        2,
        WeightingMode.RadixComplement,
        description: "Top bit has weight −2^(n−1); range −2^(n−1)..2^(n−1)−1.");

    public static PositionalFormat OnesComplement { get; } = new(
        "ones-complement",
        "One's complement",
        Signals.StandardDomains.BinaryId,
        [0, 1],
        2,
        WeightingMode.DiminishedRadixComplement,
        description: "Top bit has weight −(2^(n−1)−1); range −(2^(n−1)−1)..2^(n−1)−1; two zeros.");

    public static SignMagnitudeFormat SignMagnitude { get; } = new(
        "sign-magnitude",
        "Sign-magnitude",
        Signals.StandardDomains.BinaryId,
        [0, 1],
        2,
        negativeSignValue: 1,
        description: "Top bit 1 means negative; remainder is magnitude; range −(2^(n−1)−1)..2^(n−1)−1.");

    public static PositionalFormat UnsignedTernary { get; } = new(
        "unsigned-ternary",
        "Unsigned ternary",
        Signals.StandardDomains.TernaryId,
        [0, 1, 2],
        3,
        description: "Σ trit_i × 3^i; range 0..3^n−1.");

    public static PositionalFormat UnsignedQuaternary { get; } = new(
        "unsigned-quaternary",
        "Unsigned quaternary",
        Signals.StandardDomains.QuaternaryId,
        [0, 1, 2, 3],
        4,
        description: "Σ digit_i × 4^i; range 0..4^n−1.");

    public static PositionalFormat TernaryTopNegative { get; } = new(
        "ternary-top-negative",
        "Ternary radix complement (top digit negative)",
        Signals.StandardDomains.TernaryId,
        [0, 1, 2],
        3,
        WeightingMode.RadixComplement,
        description: "Top trit has weight −3^(n−1). Documented weight convention, not a symmetric three's-complement system.");

    public static PositionalFormat BalancedTernary { get; } = new(
        "balanced-ternary",
        "Balanced ternary",
        Signals.StandardDomains.BalancedTernaryId,
        [-1, 0, 1],
        3,
        description: "Digits map −,0,+ to −1,0,1; range −(3^n−1)/2..(3^n−1)/2.");

    public static IReadOnlyList<INumericFormat> All { get; } =
    [
        UnsignedBinary,
        TwosComplement,
        OnesComplement,
        SignMagnitude,
        UnsignedTernary,
        UnsignedQuaternary,
        TernaryTopNegative,
        BalancedTernary,
    ];

    public static INumericFormat? Find(string formatId) =>
        All.FirstOrDefault(format => string.Equals(format.Id, formatId, StringComparison.Ordinal));
}
