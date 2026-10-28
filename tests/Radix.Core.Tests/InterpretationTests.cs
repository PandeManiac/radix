using Radix.Core.Interpretation;
using Xunit;

namespace Radix.Core.Tests;

public sealed class InterpretationTests
{
    [Fact]
    public void UnsignedBinaryDecodesLsbFirst()
    {
        NumericDecodeResult result = NumericFormats.UnsignedBinary.Decode([0, 1, 0, 1]);
        Assert.True(result.IsValid);
        Assert.Equal(10, (int)result.Value);
        Assert.Equal(0, (int)NumericFormats.UnsignedBinary.MinimumValue(4));
        Assert.Equal(15, (int)NumericFormats.UnsignedBinary.MaximumValue(4));
    }

    [Theory]
    [InlineData(new[] { 0, 0, 0, 0 }, 0)]
    [InlineData(new[] { 1, 0, 0, 0 }, 1)]
    [InlineData(new[] { 0, 0, 0, 1 }, -8)]
    [InlineData(new[] { 1, 1, 1, 1 }, -1)]
    [InlineData(new[] { 1, 0, 1, 0 }, 5)]
    public void TwosComplementFollowsWeightConvention(int[] bitsLsbFirst, int expected)
    {
        NumericDecodeResult result = NumericFormats.TwosComplement.Decode(bitsLsbFirst);
        Assert.True(result.IsValid);
        Assert.Equal(expected, (int)result.Value);
    }

    [Fact]
    public void TwosComplementRangeIsAsymmetric()
    {
        Assert.Equal(-8, (int)NumericFormats.TwosComplement.MinimumValue(4));
        Assert.Equal(7, (int)NumericFormats.TwosComplement.MaximumValue(4));
    }

    [Fact]
    public void OnesComplementHasTwoZerosAndSymmetricRange()
    {
        Assert.Equal(0, (int)NumericFormats.OnesComplement.Decode([1, 1, 1, 1]).Value);
        Assert.Equal(-7, (int)NumericFormats.OnesComplement.Decode([0, 0, 0, 1]).Value);
        Assert.Equal(7, (int)NumericFormats.OnesComplement.Decode([1, 1, 1, 0]).Value);
        Assert.Equal(-7, (int)NumericFormats.OnesComplement.MinimumValue(4));
        Assert.Equal(7, (int)NumericFormats.OnesComplement.MaximumValue(4));
    }

    [Fact]
    public void SignMagnitudeDecodesNegativeZero()
    {
        Assert.Equal(-1, (int)NumericFormats.SignMagnitude.Decode([1, 0, 0, 1]).Value);
        Assert.Equal(0, (int)NumericFormats.SignMagnitude.Decode([0, 0, 0, 1]).Value);
        Assert.Equal(-7, (int)NumericFormats.SignMagnitude.MinimumValue(4));
        Assert.Equal(7, (int)NumericFormats.SignMagnitude.MaximumValue(4));
    }

    [Fact]
    public void UnsignedTernaryDecodes()
    {
        Assert.Equal(26, (int)NumericFormats.UnsignedTernary.Decode([2, 2, 2]).Value);
        Assert.Equal(0, (int)NumericFormats.UnsignedTernary.MinimumValue(3));
        Assert.Equal(26, (int)NumericFormats.UnsignedTernary.MaximumValue(3));
    }

    [Fact]
    public void BalancedTernaryDecodes()
    {
        // least significant first: +, +, 0 -> 1 + 3 = 4
        Assert.Equal(4, (int)NumericFormats.BalancedTernary.Decode([2, 2, 1]).Value);
        Assert.Equal(-13, (int)NumericFormats.BalancedTernary.MinimumValue(3));
        Assert.Equal(13, (int)NumericFormats.BalancedTernary.MaximumValue(3));
    }

    [Fact]
    public void TernaryTopNegativeUsesDocumentedWeight()
    {
        // low digit 2, top digit 1 contributing 1 * -3 = -1
        NumericDecodeResult result = NumericFormats.TernaryTopNegative.Decode([2, 1]);
        Assert.True(result.IsValid);
        Assert.Equal(-1, (int)result.Value);
        Assert.Equal(-6, (int)NumericFormats.TernaryTopNegative.MinimumValue(2));
        Assert.Equal(2, (int)NumericFormats.TernaryTopNegative.MaximumValue(2));
    }

    [Fact]
    public void UndrivenAndUnmappedSignalsAreInvalid()
    {
        Assert.False(NumericFormats.UnsignedBinary.Decode([0, -1]).IsValid);
        Assert.False(NumericFormats.UnsignedBinary.Decode([0, 2]).IsValid);
        Assert.Contains("undriven", NumericFormats.UnsignedBinary.Decode([0, -1]).Error);
    }

    [Fact]
    public void MsbFirstCustomFormatReversesSignificance()
    {
        var format = new PositionalFormat("msb", "MSB first", "binary", [0, 1], 2, leastSignificantFirst: false);
        Assert.Equal(5, (int)format.Decode([1, 0, 1]).Value);
        Assert.Equal(11, (int)format.Decode([1, 0, 1, 1]).Value);
    }

    [Fact]
    public void SignedMagnitudeRejectsNegativeDigits()
    {
        Assert.Throws<ArgumentException>(() =>
            new SignMagnitudeFormat("x", "X", "binary", [-1, 1], 2, negativeSignValue: 1));
    }

    [Fact]
    public void DescribeIncludesRange()
    {
        string description = NumericFormats.TwosComplement.Describe(8);
        Assert.Contains("-128", description);
        Assert.Contains("127", description);
    }

    [Fact]
    public void InterpretationHasNoSimulationDependency()
    {
        // A pure value transformation: repeated calls are stable and stateless.
        int[] bits = [1, 0, 1, 1];
        NumericDecodeResult first = NumericFormats.TwosComplement.Decode(bits);
        NumericDecodeResult second = NumericFormats.TwosComplement.Decode(bits);
        Assert.Equal(first, second);
    }
}
