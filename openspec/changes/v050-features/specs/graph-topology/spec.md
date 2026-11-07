## ADDED Requirements

### Requirement: Node options on every registration overload
The builder MUST accept `NodeOptions` on every node registration overload (handler, DI node, `IGraphNode` instance, node factory). `NodeOptions.Retry` carries a `RetryPolicy`.

#### Scenario: Retry declared on handler node
- **WHEN** a graph registers a node with `AddNode(name, handler, new NodeOptions { Retry = RetryPolicy.Exponential(3, TimeSpan.FromSeconds(1)) })` and compiles
- **THEN** compilation succeeds and the node is configured for up to 3 total executions

### Requirement: Retry policy declaration validated
`RetryPolicy.Exponential(maxAttempts, baseDelay, retryOn?)` declares up to `maxAttempts` total executions (1 = no retry). Compile-time validation MUST reject `maxAttempts < 1` with a stable error code and MUST accept a custom `retryOn` predicate.

#### Scenario: Invalid max attempts rejected at compile
- **WHEN** a graph registers a node with `RetryPolicy.Exponential(0, TimeSpan.FromSeconds(1))` and compiles
- **THEN** compilation fails with a stable error and the graph cannot be run

### Requirement: Wait-for-all join edges
The builder MUST accept `AddEdge(string[] sources, string target)` declaring a join: `target` becomes ready only after every node in `sources` has completed in the current join epoch. Compile-time validation MUST reject empty or duplicate `sources`, unknown or sentinel endpoints, and a `target` that also has other incoming static or join edges. A source node MAY keep other outgoing edges.

#### Scenario: Join target waits for all sources
- **WHEN** a graph has `AddEdge(new[] { "a", "b" }, "c")` and only `a` has completed after a superstep
- **THEN** `c` is not scheduled

#### Scenario: Duplicate or empty sources rejected at compile
- **WHEN** a graph declares `AddEdge(new[] { "a", "a" }, "c")` or `AddEdge(Array.Empty<string>(), "c")` and compiles
- **THEN** compilation fails with a stable error

#### Scenario: Join target with mixed in-edges rejected at compile
- **WHEN** a target has both a single-source `AddEdge("a", "t")` and a join `AddEdge(new[] { "b", "c" }, "t")`
- **THEN** compilation fails with a stable error
