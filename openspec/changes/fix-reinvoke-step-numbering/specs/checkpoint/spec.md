## ADDED Requirements

### Requirement: Run entry continues the thread's step numbering
A runtime run MUST append to the thread's history instead of rewriting it: when the thread already holds a checkpoint whose latest step is N, the run's first put MUST be guarded with expectedParentStep = N and MUST land at step N+1, and the run's later checkpoints MUST continue from there (N+2, N+3, ...). When the thread holds no checkpoint, the run's first put MUST stay a blind write at step 0 and the first superstep MUST land at step 1.

#### Scenario: Repeated run on a thread with history continues the numbering
- **WHEN** a run is started on a thread whose latest checkpoint is at step N
- **THEN** the run's first checkpoint is written at step N+1 with an expected parent step of N, the following checkpoints continue at N+2 and later, and no conflict is raised

#### Scenario: History of a repeatedly invoked thread grows monotonically
- **WHEN** the same thread is invoked several times in sequence
- **THEN** the thread's listed history holds non-decreasing steps, every invocation appends new checkpoints whose steps all exceed the previous invocation's last step, and the checkpoints of the earlier runs are still readable

#### Scenario: Fresh thread keeps the blind first put
- **WHEN** a run is started on a thread that holds no checkpoint
- **THEN** the first checkpoint is written at step 0 without an expected parent step and the first superstep is written at step 1

### Requirement: Concurrent runs on one thread have exactly one winner
Two runs that write to the same thread at the same time MUST NOT both succeed: exactly one run MUST advance the thread's history, and the other MUST fail with CheckpointStoreException code checkpoint.conflict without persisting anything.

#### Scenario: Racing runs on one thread
- **WHEN** two runs are started concurrently on the same thread that already holds a checkpoint
- **THEN** exactly one run completes and the other fails with CheckpointStoreException code checkpoint.conflict
