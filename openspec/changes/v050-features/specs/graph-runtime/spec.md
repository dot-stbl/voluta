## MODIFIED Requirements

### Requirement: Recursion limit
The runtime MUST stop a run that exceeds a configured maximum superstep count and surface a distinct out-of-steps failure (not a silent hang). The runtime MUST treat the sentinel value `RecursionLimit.Unlimited` explicitly as "no superstep limit" — the comparison against the limit MUST be skipped in that mode rather than relying on numeric overflow — while the shipped default limit and the out-of-steps failure behavior remain unchanged.

#### Scenario: Infinite cycle
- **WHEN** a cycle never reaches END and the superstep count exceeds the configured limit
- **THEN** the run fails with an out-of-steps error and the last successful checkpoint remains loadable

#### Scenario: Unlimited mode runs past the default bound
- **WHEN** a graph is compiled via `CompileOptions.WithoutRecursionLimit()` (or `RecursionLimit = RecursionLimit.Unlimited`) and runs more supersteps than the shipped default limit on a cycle that eventually reaches END
- **THEN** the run completes Done without an out-of-steps failure

## ADDED Requirements

### Requirement: Node retry execution
For a node configured with a retry policy, the runtime MUST re-execute the task body until it succeeds, returns an interrupt, the retry predicate rejects the exception, or `maxAttempts` total executions are reached; the final exception follows the ordinary node-failure path. The 1-based attempt number MUST be visible to the node as `GraphContext.Attempt`, and inter-attempt waiting MUST honor cancellation.

#### Scenario: Flaky node succeeds on third attempt
- **WHEN** a node with `maxAttempts = 3` fails twice with a retryable exception and succeeds on the third invocation
- **THEN** the superstep commits the third attempt's writes and the node observes `GraphContext.Attempt` values 1, 2, 3 across its invocations

#### Scenario: Exhausted retries fail the superstep
- **WHEN** every attempt of a retried node throws a retryable exception
- **THEN** the run follows the ordinary failure path (Failed snapshot, failure stream event, thrown exception)

#### Scenario: Default retry predicate excludes cancellation
- **WHEN** a node with a retry policy fails with an `OperationCanceledException` and no explicit `retryOn` was supplied
- **THEN** the failure is not retried and cancellation semantics are unchanged

### Requirement: Durable retry attempt
The in-progress attempt number MUST be persisted in checkpoints so that a crash during attempt N resumes execution at attempt N+1 on the recovered thread.

#### Scenario: Crash during a retry resumes at the next attempt
- **WHEN** a task is executing attempt 3 of 5 and the process crashes before the task completes, then the thread is resumed
- **THEN** the recovered run executes that task as attempt 4, not attempt 1

### Requirement: Per-task durable writes
After each task of a superstep completes, the runtime MUST record its channel writes and a completed-task mark in a checkpoint put for the current step — for all tasks of the superstep. On restart, the runtime MUST apply recorded writes and re-execute only unfinished tasks. Recorded writes MUST NOT be visible before the barrier, and concurrent completions MUST NOT lose completion marks.

#### Scenario: Mid-superstep crash re-runs only unfinished tasks
- **WHEN** two parallel tasks run and only one finishes (its writes and completion mark recorded) before a crash, then the thread is resumed
- **THEN** the finished task is not re-executed, its recorded writes are applied to the restored state, and only the other task executes

### Requirement: Clean step-boundary checkpoint
A checkpoint committed at a step boundary MUST carry no progress residue: completed-task marks from the finished superstep are cleared and pending writes of fully applied work are empty.

#### Scenario: Clean superstep leaves no progress residue
- **WHEN** all tasks of a superstep complete and the superstep commits normally
- **THEN** the committed checkpoint contains no incomplete-task residue

### Requirement: Wait-for-all join barrier
For a declared join edge, the runtime MUST schedule the target only after all of its sources have completed in the current join epoch, and arrival state MUST be durable on checkpoints. After the target executes, the barrier MUST reopen so a later pass of the sources triggers it again, and the target MUST fire at most once per epoch. Ordinary LastValue write-conflict rules apply unchanged.

#### Scenario: Durable arrivals across a crash
- **WHEN** source `a` of join (a, b → c) has completed and its arrival is recorded, the process crashes, and the thread is resumed
- **THEN** source `b` alone completing is enough to schedule `c`

#### Scenario: Epoch reopens after the join fires
- **WHEN** join target `c` has executed and the sources complete again through a cycle
- **THEN** `c` is scheduled again

### Requirement: Stop guard evaluation
The runtime MUST evaluate a configurable set of stop guards (`IRunGuard`) before scheduling each superstep, at every engine entry that schedules supersteps, and guard evaluation MUST precede the out-of-steps check. A guard stop MUST park the run with an `Interrupted` snapshot (no new run status) and MUST NOT fail the run; `ContinueAsync` on such a snapshot MUST resume scheduling.

#### Scenario: Deadline stops a long cycle cleanly
- **WHEN** a cyclic graph runs with `RunGuards.Deadline(...)` and the deadline elapses before the next superstep
- **THEN** the run parks Interrupted with StopReason `DeadlineExceeded`, and a later `ContinueAsync` resumes execution

### Requirement: Guard stop snapshot shape
A guard stop snapshot MUST record a typed `StopReason` (`BudgetExhausted` for `RunGuards.Budget`, `DeadlineExceeded` for `RunGuards.Deadline`), keep the pending ready set in `NextNodes`, and leave `PendingInterrupts` empty.

#### Scenario: Budget stop maps to its reason
- **WHEN** a guard created by `RunGuards.Budget(...)` returns stop
- **THEN** the snapshot carries StopReason `BudgetExhausted`, the ready set is preserved, and no out-of-steps error is thrown
