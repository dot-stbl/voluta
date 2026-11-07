using Shouldly;
using Voluta.Abstractions.Runtime;
using Xunit;

namespace Voluta.Abstractions.Unit.Runtime;

public sealed class RunGuardsShould
{
    private static RunGuardContext Context(DateTimeOffset? startedAt = null)
    {
        return new RunGuardContext
        {
            NextStep = 4,
            CompletedSteps = 3,
            StartedAt = startedAt ?? DateTimeOffset.UtcNow,
        };
    }

    [Fact(DisplayName = "Given decisions, when inspected, then Continue/Stop carry action and reason")]
    public void ExposeDecisionShapes()
    {
        RunGuardDecision.Continue.Action.ShouldBe(RunGuardAction.Continue);
        RunGuardDecision.Continue.Reason.ShouldBeNull();
        var stop = RunGuardDecision.Stop("budget_exhausted");
        stop.Action.ShouldBe(RunGuardAction.Stop);
        stop.Reason.ShouldBe("budget_exhausted");
    }

    [Fact(DisplayName = "Given future absolute deadline, when evaluated, then Continue; past deadline Stops with deadline_exceeded")]
    public void AbsoluteDeadlineGuardParks()
    {
        var now = DateTimeOffset.UtcNow;
        var future = RunGuards.Deadline(now.AddMinutes(5));
        var past = RunGuards.Deadline(now.AddMinutes(-5));

        future.Evaluate(Context(now)).Action.ShouldBe(RunGuardAction.Continue);
        var decision = past.Evaluate(Context(now));
        decision.Action.ShouldBe(RunGuardAction.Stop);
        decision.Reason.ShouldBe(RunGuards.DeadlineExceeded);
    }

    [Fact(DisplayName = "Given TimeSpan deadline already elapsed at construction, when evaluated, then Stops")]
    public void RelativeDeadlineAnchorsAtConstruction()
    {
        var guard = RunGuards.Deadline(TimeSpan.FromMilliseconds(-1));

        var decision = guard.Evaluate(Context());

        decision.Action.ShouldBe(RunGuardAction.Stop);
        decision.Reason.ShouldBe(RunGuards.DeadlineExceeded);
    }

    [Fact(DisplayName = "Given budget predicate returning false then true, when evaluated, then Continue then budget_exhausted")]
    public void BudgetGuardParksWhenExhausted()
    {
        var exhausted = false;
        var guard = RunGuards.Budget(_ => exhausted);

        guard.Evaluate(Context()).Action.ShouldBe(RunGuardAction.Continue);

        exhausted = true;
        var decision = guard.Evaluate(Context());
        decision.Action.ShouldBe(RunGuardAction.Stop);
        decision.Reason.ShouldBe(RunGuards.BudgetExhausted);
    }

    [Fact(DisplayName = "Given context, when guard evaluates, then NextStep/CompletedSteps/StartedAt are visible")]
    public void ExposeContextValues()
    {
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        RunGuardContext? observed = null;
        var guard = RunGuards.Budget(context =>
        {
            observed = context;
            return false;
        });

        guard.Evaluate(Context(startedAt)).Action.ShouldBe(RunGuardAction.Continue);

        observed.ShouldNotBeNull();
        observed.NextStep.ShouldBe(4);
        observed.CompletedSteps.ShouldBe(3);
        observed.StartedAt.ShouldBe(startedAt);
    }
}
