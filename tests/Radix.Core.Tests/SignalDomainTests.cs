using Radix.Core.Signals;
using Xunit;

namespace Radix.Core.Tests;

public sealed class SignalDomainTests
{
    [Fact]
    public void BinaryDomainHasTwoOrdinaryValues()
    {
        SignalDomain domain = StandardDomains.Binary();
        Assert.Equal(2, domain.ValueCount);
        Assert.Equal(["0", "1"], domain.ValueNames);
        Assert.Equal("1", domain.ValueName(1));
        Assert.False(domain.IsMultipleDriverPolicy);
    }

    [Fact]
    public void SingleDriverPolicyReportsConflicts()
    {
        SignalDomain domain = StandardDomains.Binary();
        Assert.Equal(-1, domain.Drivers.Resolve(domain, []).Value);

        DriverResolution single = domain.Drivers.Resolve(domain, [1]);
        Assert.Equal(1, single.Value);
        Assert.False(single.Conflict);

        DriverResolution many = domain.Drivers.Resolve(domain, [0, 1]);
        Assert.Equal(0, many.Value);
        Assert.True(many.Conflict);
    }

    [Fact]
    public void FourStatePriorityResolutionFollowsDocumentedPriorities()
    {
        SignalDomain domain = StandardDomains.FourState();

        Assert.Equal(0, domain.Drivers.Resolve(domain, [3, 0]).Value); // Z,0
        Assert.Equal(1, domain.Drivers.Resolve(domain, [3, 1]).Value); // Z,1
        Assert.Equal(2, domain.Drivers.Resolve(domain, [0, 1]).Value); // conflict -> X
        Assert.True(domain.Drivers.Resolve(domain, [0, 1]).Conflict);

        DriverResolution xWins = domain.Drivers.Resolve(domain, [2, 1]);
        Assert.Equal(2, xWins.Value);
        Assert.False(xWins.Conflict);

        Assert.Equal(3, domain.Drivers.Resolve(domain, [3, 3]).Value);
    }

    [Fact]
    public void PriorityDomainRejectsMismatchedTable()
    {
        Assert.Throws<ArgumentException>(() =>
            new SignalDomain("x", "X", ["0", "1"], new PriorityDriverResolution([1, 2, 3], 0)));
    }

    [Fact]
    public void DomainRequiresValues()
    {
        Assert.Throws<ArgumentException>(() =>
            new SignalDomain("x", "X", [], new SingleDriverResolution()));
    }

    [Fact]
    public void CustomFiniteDomainIsSupported()
    {
        var domain = new SignalDomain(
            "custom-four",
            "Custom four-level",
            ["low", "mid", "high", "float"],
            new PriorityDriverResolution([2, 3, 4, 0], 1));

        Assert.Equal(4, domain.ValueCount);
        Assert.Equal(0, domain.Drivers.Resolve(domain, [3, 0]).Value);
        Assert.Equal(3, domain.Drivers.Resolve(domain, [3, 3]).Value);
        Assert.Equal(1, domain.Drivers.Resolve(domain, [0, 1]).Value);
    }

    [Fact]
    public void SignalValueDistinguishesUndrivenFromIndexZero()
    {
        Assert.False(SignalValue.Undriven.IsDriven);
        Assert.True(new SignalValue(0).IsDriven);
        Assert.Equal("undriven", SignalValue.Undriven.ToString());
    }
}
