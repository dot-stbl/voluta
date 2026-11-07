## ADDED Requirements

### Requirement: CI unit matrix by explicit project list
CI MUST run unit tests via an explicit list of unit test projects (not a `FullyQualifiedName!~Integration` name filter over the whole solution), so every unit test project — including provider and tooling unit projects — is guaranteed to run and integration projects are never accidentally included.

#### Scenario: New unit project is picked up or fails loudly
- **WHEN** a unit test project is added to the repository
- **THEN** either it is listed in the CI test step or CI fails the PR review visibly, and no integration test executes in the unit job

### Requirement: CI failure diagnostics and bounded jobs
CI test steps MUST emit TRX logs and upload them as build artifacts when the test step fails, and every CI job MUST declare `timeout-minutes` so a hung job cannot consume the runner indefinitely.

#### Scenario: Test failure yields TRX artifact
- **WHEN** a unit test run fails in CI
- **THEN** a TRX artifact is uploaded and downloadable from the run

#### Scenario: Job cannot hang forever
- **WHEN** a CI job hangs
- **THEN** the job is terminated by its declared timeout and marked failed

### Requirement: Pack smoke packs only packable projects
The CI pack smoke step MUST NOT attempt to pack projects with `IsPackable=false` (such as `Voluta.Testing`) and MUST still verify that every shippable library project produces a nupkg.

#### Scenario: Pack smoke passes with non-packable projects present
- **WHEN** CI runs pack smoke on a tree containing `IsPackable=false` projects
- **THEN** the step packs only packable projects and passes

### Requirement: Publish verifies tag against package versions
The publish workflow MUST verify, before pushing, that the version of every produced nupkg matches the `v*.*.*` tag that triggered the run, and MUST fail the release when they disagree (a tag like `v0.5.1` over props `0.5.0` must not silently publish 0.5.0 packages).

#### Scenario: Tag and package version mismatch blocks publish
- **WHEN** a `v0.5.1` tag triggers publish while `Directory.Build.props` still declares `0.5.0`
- **THEN** the workflow fails before any push and nothing is published

### Requirement: Publish credential policy is explicit and safe
The publish workflow MUST document and implement exactly one default push path — API key via `secrets.NUGET_API_KEY` — with the workflow header honestly describing it. An OIDC trusted-publishing login step MAY exist but MUST be gated behind `vars.NUGET_OIDC == 'true'` and MUST be mutually exclusive with the API-key path.

#### Scenario: OIDC flag off keeps API-key push
- **WHEN** a tag triggers publish while `vars.NUGET_OIDC` is unset
- **THEN** packages push with the API key and no OIDC login executes

### Requirement: Dispatch push is opt-in
Push steps MUST run for tag pushes and, for `workflow_dispatch` runs, only when the caller explicitly opts in with `dry_run = false`; dispatch with `dry_run = true` (the default) MUST never push or upload.

#### Scenario: Dispatch default never pushes
- **WHEN** a maintainer runs the workflow via `workflow_dispatch` without setting `dry_run`
- **THEN** packages are packed but nothing is pushed to nuget.org and nothing is uploaded to a release

### Requirement: Changelog maintained per release
The repository MUST maintain a `CHANGELOG.md` in Keep a Changelog format with an entry for the upcoming version (Unreleased until tagged), and the publish workflow MUST use the tagged version's changelog section as GitHub release notes.

#### Scenario: Release notes come from the changelog
- **WHEN** a `v*.*.*` tag triggers publish
- **THEN** the created GitHub Release body contains the corresponding changelog section
