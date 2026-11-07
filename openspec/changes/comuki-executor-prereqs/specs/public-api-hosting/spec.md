# Spec Delta

## ADDED Requirements

### Requirement: Resolver-based multi-version graph registration
The DI composition root MUST offer a registration that hosts a set of graphs behind a per-thread resolver:
- The resolver contract (`IGraphResolver`) MUST live in the core runtime package (not the abstractions package, which does not reference compiled graphs).
- The registration API (`VolutaBuilder.Graphs(...)`) MUST register the resolver as a singleton and MUST NOT register a `CompiledGraph` — hosts that opt into resolver hosting resolve `IGraphResolver`, not a concrete graph.
- The existing single-graph registration (`VolutaBuilder.Graph(...)`) MUST keep working unchanged in observable behavior AND MUST additionally auto-register a default resolver over that graph (so resolver-based consumers and single-graph consumers coexist); the auto-registered default resolver returns the registered graph and, for snapshots whose identity pair differs from it, fails resume/continue with `graph.version_mismatch`.

#### Scenario: Graphs registration exposes resolver only
- **WHEN** a host registers graphs via the resolver-based registration API
- **THEN** `IGraphResolver` resolves as a singleton from the container and resolving `CompiledGraph` does not return a graph registered that way

#### Scenario: Resolver resolves default for new thread
- **WHEN** a host registered a default graph plus versioned variants and a run starts for a thread without a checkpoint
- **THEN** the resolver returns the default graph for that thread

#### Scenario: Resolver resolves stored identity
- **WHEN** a thread's snapshot carries an identity pair that a registered graph matches
- **THEN** the resolver returns that graph for the thread's run entries

#### Scenario: Single-graph registration stays back-compatible
- **WHEN** a host registers a compiled graph via the existing single-graph API and resolves both the graph and the resolver
- **THEN** the registered graph resolves as before and the resolver also resolves, defaulting to that same graph

#### Scenario: Runner and UI consume the resolver
- **WHEN** the hosting worker thread runner and the ops UI session are constructed in a resolver-registered host
- **THEN** they resolve graphs through `IGraphResolver` per thread instead of a fixed injected `CompiledGraph`
