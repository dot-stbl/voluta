using Shouldly;
using Voluta.Abstractions.Runtime;
using Xunit;

namespace Voluta.Abstractions.Unit.Runtime;

public sealed class RetryPolicyShould
{
    [Fact(DisplayName = "Given maxAttempts < 1, when Exponential, then ArgumentOutOfRangeException")]
    public void RejectMaxAttemptsBelowOne()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            static () => RetryPolicy.Exponential(0, TimeSpan.FromMilliseconds(10)));
        Should.Throw<ArgumentOutOfRangeException>(
            static () => RetryPolicy.Exponential(-3, TimeSpan.FromMilliseconds(10)));
    }

    [Theory(DisplayName = "Given non-positive or near-overflow base delay, when Exponential, then ArgumentOutOfRangeException")]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectInvalidBaseDelay(long ticks)
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => RetryPolicy.Exponential(3, TimeSpan.FromTicks(ticks)));
        Should.Throw<ArgumentOutOfRangeException>(
            static () => RetryPolicy.Exponential(3, TimeSpan.MaxValue));
    }

    [Fact(DisplayName = "Given policy, when inspected, then ctor arguments are preserved")]
    public void PreservePolicyArguments()
    {
        var policy = RetryPolicy.Exponential(
            5,
            TimeSpan.FromSeconds(2),
            static exception => exception is InvalidOperationException);

        policy.MaxAttempts.ShouldBe(5);
        policy.BaseDelay.ShouldBe(TimeSpan.FromSeconds(2));
        policy.RetryOn.ShouldNotBeNull();
        policy.RetryOn!(new InvalidOperationException()).ShouldBeTrue();
        policy.RetryOn(new ApplicationException()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Given no predicate, when ShouldRetry with cancellation, then false; other exceptions true")]
    public void DefaultPredicateExcludesCancellation()
    {
        var policy = RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(1));

        policy.ShouldRetry(new OperationCanceledException()).ShouldBeFalse();
        policy.ShouldRetry(new InvalidOperationException()).ShouldBeTrue();
    }

    [Fact(DisplayName = "Given custom predicate, when ShouldRetry, then predicate decides")]
    public void CustomPredicateDecides()
    {
        var policy = RetryPolicy.Exponential(
            3,
            TimeSpan.FromMilliseconds(1),
            static exception => exception is InvalidOperationException);

        policy.ShouldRetry(new InvalidOperationException()).ShouldBeTrue();
        policy.ShouldRetry(new OperationCanceledException()).ShouldBeFalse();
        policy.ShouldRetry(new ApplicationException()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Given completed attempts, when DelayFor, then exponential backoff")]
    public void GrowExponentially()
    {
        var baseDelay = TimeSpan.FromMilliseconds(10);
        var policy = RetryPolicy.Exponential(10, baseDelay);

        policy.DelayFor(1).ShouldBe(TimeSpan.FromMilliseconds(10));
        policy.DelayFor(2).ShouldBe(TimeSpan.FromMilliseconds(20));
        policy.DelayFor(3).ShouldBe(TimeSpan.FromMilliseconds(40));
        policy.DelayFor(5).ShouldBe(TimeSpan.FromMilliseconds(160));
    }

    [Fact(DisplayName = "Given huge attempt number or near-overflow base, when DelayFor, then never throws and stays positive")]
    public void CapGrowthWithoutOverflow()
    {
        var policy = RetryPolicy.Exponential(100, TimeSpan.FromHours(24));

        policy.DelayFor(1).ShouldBeGreaterThan(TimeSpan.Zero);
        policy.DelayFor(200).ShouldBeGreaterThan(TimeSpan.Zero);
        policy.DelayFor(10_000).ShouldBeGreaterThan(TimeSpan.Zero);
        Should.NotThrow(() => policy.DelayFor(int.MaxValue));
    }
}
