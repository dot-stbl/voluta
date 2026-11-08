## ADDED Requirements

### Requirement: Repeated invocation restarts the graph but keeps the thread's numbering
A repeated invocation of a thread MUST start the graph from its entry with the caller's input writes only (the run does not observe the previous run's channel values) and MUST continue the thread's step numbering from the thread's latest checkpoint. It MUST NOT fail merely because the thread already holds history.

#### Scenario: Second invocation on the same thread succeeds
- **WHEN** a thread is invoked a second time after a completed run
- **THEN** the second run executes the graph from its entry, writes its checkpoints after the previous run's steps, and completes normally

#### Scenario: Input of the repeated run is applied, previous state is not
- **WHEN** a thread is invoked again with new input writes
- **THEN** the repeated run starts from the graph's entry with the new input values and does not observe the previous run's channel values

### Requirement: Invocation validates the graph identity of the thread's latest snapshot
An invocation on a thread that already holds a checkpoint MUST compare the checkpoint's graph identity pair with the executing graph's pair before writing anything: a differing pair MUST fail with the stable code graph.version_mismatch, and a checkpoint that carries no graph version MUST be accepted without a check, exactly as on resume and continue.

#### Scenario: Foreign identity rejected before the first write
- **WHEN** a thread whose latest checkpoint carries a foreign identity pair is invoked again
- **THEN** the invocation fails with graph.version_mismatch and the thread's checkpoint is left unchanged

#### Scenario: Legacy snapshot accepted without a check
- **WHEN** a thread whose latest checkpoint carries no graph version is invoked again
- **THEN** the invocation proceeds normally
