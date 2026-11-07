# Spec Delta

## MODIFIED Requirements

### Requirement: Checkpoint conformance suite
A shared conformance suite MUST express the behavioral contract of `ICheckpointer` (roundtrip C-shape, missing thread, interrupt fields, pending writes when applicable, optimistic-concurrency put semantics) and MUST run against InMemory in CI. The suite MUST additionally cover the guarded-put semantics: expected-parent-step acceptance, expected-parent-step mismatch conflict, same-step concurrent rewrite conflict, blind put when the expected parent is omitted, and additive-field compatibility (snapshots without graph identity / cancel-reason fields). The conflict scenarios MUST run against every shipped durable provider (File, Redis, S3, EntityFrameworkCore) as well as InMemory, via each provider's existing conformance hookup.

#### Scenario: InMemory passes conformance
- **WHEN** the conformance suite executes against InMemory
- **THEN** all mandatory conformance tests pass in CI

#### Scenario: Future provider reuses suite
- **WHEN** an EF Core checkpointer package is added later
- **THEN** the same suite can be pointed at that provider without rewriting scenarios

#### Scenario: Guarded put conflict in conformance
- **WHEN** the suite runs a guarded put whose expected parent step no longer matches the thread's latest step
- **THEN** the provider under test throws `CheckpointStoreException` with code `checkpoint.conflict`

#### Scenario: Same step rewrite race in conformance
- **WHEN** the suite drives two overlapping puts to the same step key on a guarded write
- **THEN** exactly one put succeeds and the other fails with code `checkpoint.conflict`

#### Scenario: Blind put unchanged in conformance
- **WHEN** the suite puts a snapshot without an expected parent step
- **THEN** the provider writes it unconditionally, matching pre-change behavior

#### Scenario: Additive fields tolerated in conformance
- **WHEN** the suite roundtrips snapshots that lack graph identity and cancel-reason fields, and snapshots that carry them
- **THEN** both roundtrip without loss on every provider, with the wire format version unchanged

#### Scenario: Conflict scenarios run for durable providers
- **WHEN** the provider conformance suites execute for File, Redis, S3, and EntityFrameworkCore
- **THEN** the guarded-put conflict scenarios run against each of them and pass
