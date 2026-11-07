# Spec Delta

## ADDED Requirements

### Requirement: Compile-time graph identity
A compiled graph MUST carry an optional host-assigned identity: `GraphId` and `GraphVersion`, both strings, supplied through compile options. The identity MUST be stamped onto every checkpoint snapshot the graph's runs write. Graphs compiled without an identity MUST produce snapshots without identity fields (null), and MUST NOT fail.

#### Scenario: Identity from compile options lands on snapshots
- **WHEN** a graph is compiled with a graph id and version and a run checkpoints
- **THEN** every snapshot written by that run carries the same id/version pair

#### Scenario: Compiling without identity stays legal
- **WHEN** a graph is compiled without a graph id or version
- **THEN** compilation and runs succeed and written snapshots carry null identity fields

### Requirement: Per-thread graph resolution
The runtime MUST support selecting the executing graph per thread via a resolver abstraction that receives the thread id and the thread's latest snapshot (or null for a new thread) and returns the graph to execute. Resolution rules:
- Snapshot is null (new thread) → the resolver returns the host's default graph.
- Snapshot carries a graph identity pair → the resolver returns the graph matching that pair.
The runtime MUST resolve through the resolver at the start of every run entry (invoke, stream, resume, continue) rather than assuming a fixed compiled graph, when the host registered a resolver. When no resolver is registered (plain single-graph registration), the compiled graph executes as before.

#### Scenario: New thread resolves default graph
- **WHEN** a run starts for a thread with no checkpoint and a resolver is registered
- **THEN** the resolver is asked with a null snapshot and the returned default graph executes

#### Scenario: Existing thread resolves by snapshot identity
- **WHEN** a run entry starts for a thread whose snapshot carries a graph identity pair and a resolver is registered
- **THEN** the resolver receives the snapshot and the returned graph (matching that pair) executes the run

#### Scenario: No resolver keeps single-graph behavior
- **WHEN** the host registered a single compiled graph without a resolver
- **THEN** all run entries execute that graph without resolution

### Requirement: Resume and continue validate graph identity
Resume and continue entries MUST compare the snapshot's graph identity pair against the identity of the graph that will execute the run:
- A snapshot without identity fields (legacy snapshot from earlier versions) MUST be accepted and resumed WITHOUT any identity check — upgrading a deployment must not orphan existing parked threads.
- A snapshot whose identity pair differs from the executing graph's pair MUST fail with a new stable error code `graph.version_mismatch`, before any node executes and without mutating the checkpoint.
- A snapshot whose identity pair matches MUST proceed as today.

#### Scenario: Legacy snapshot resumes without check
- **WHEN** a thread holds an interrupted snapshot that predates graph identity fields and the host resumes it
- **THEN** the run proceeds normally even though the executing graph has a non-null identity pair

#### Scenario: Mismatched version rejected
- **WHEN** a thread's snapshot carries identity pair (id, v1) and the resolving graph's pair is (id, v2)
- **THEN** resume (or continue) fails with code `graph.version_mismatch` and the checkpoint is left unchanged

#### Scenario: Matching pair proceeds
- **WHEN** a thread's snapshot identity pair equals the resolving graph's pair
- **THEN** the run resumes/continues normally
