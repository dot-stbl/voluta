## ADDED Requirements

### Requirement: Static interrupt points
Hosts MUST be able to declare static pause points via `CompileOptions.InterruptBefore`/`InterruptAfter` (known node names, validated at compile). A before-point MUST park the run Interrupted before the named node executes, with the ready node preserved in `NextNodes`; an after-point MUST park after the node's output is applied and routed. The pause MUST re-arm on every pass through the point, including via cycles.

#### Scenario: Pause after node output
- **WHEN** a graph is compiled with `InterruptAfter = ["worker"]` and invoked
- **THEN** `worker` executes, its writes are applied, and the run parks with stop reason `StaticAfter` before the next superstep is scheduled

#### Scenario: Cycle re-enters the pause point
- **WHEN** a cyclic graph with a static point on a node in the cycle reaches that node a second time after a continue
- **THEN** the run parks again at the same point

### Requirement: Static pause resume
Continuing from a static pause MUST proceed with ordinary execution without requiring a command payload, and static points MUST apply at every engine entry that schedules supersteps, including resumed runs and pending-send continuation.

#### Scenario: Pause before node entry and continue
- **WHEN** a graph is compiled with `InterruptBefore = ["review"]` and invoked, and the host then continues the parked run
- **THEN** the run parks before `review` executes (snapshot Interrupted, `review` in NextNodes), and the continue executes `review` exactly once without a command payload

### Requirement: Static pauses are distinguishable from HITL interrupts
Hosts MUST be able to tell a static pause from a node (HITL) interrupt and from a guard stop using only the snapshot pair (pending interrupts, stop reason): HITL interrupts carry non-empty pending interrupts and no stop reason; static pauses carry `StaticBefore`/`StaticAfter` with empty pending interrupts; guard stops carry `BudgetExhausted`/`DeadlineExceeded`. Resuming a static pause with a per-task command payload MUST be rejected with the stable invalid-resume error.

#### Scenario: Host distinguishes pause kinds
- **WHEN** a host reads snapshots parked by a node interrupt, a static before-point, and a budget guard
- **THEN** the three snapshots are distinguishable by (pending interrupts, stop reason) alone

#### Scenario: Command payload on a static pause rejected
- **WHEN** a host calls resume with a per-task command payload while the thread is parked at a static pause
- **THEN** the resume fails with the stable invalid-resume error and the snapshot is unchanged
