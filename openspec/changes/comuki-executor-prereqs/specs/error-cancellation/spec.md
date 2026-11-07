# Spec Delta

## ADDED Requirements

### Requirement: Out-of-band cancel of a parked thread
The compiled graph MUST expose a host API that cancels a parked thread (one not currently executing in this process) by writing a `Cancelled` snapshot from the thread's last state:
- Allowed origin statuses: `Running` and `Interrupted`. The written snapshot MUST record `Cancelled` status and the host-supplied reason in the new additive `CancelReason` snapshot field.
- Cancelling an already-`Cancelled` thread MUST succeed idempotently (no error, no duplicate snapshot content change beyond a permitted guarded rewrite).
- Cancelling a thread whose latest snapshot is `Done` or `Failed` MUST fail with the new stable code `graph.invalid_cancel`.
- The write MUST be a guarded put (expected parent step = the thread's latest step) so a concurrently running execution of the same thread fails its own next put with `checkpoint.conflict` instead of silently overwriting the cancelled state.
- Missing thread (no checkpoint) MUST fail with the existing stable thread-not-found code.
- Cancel targets the parked thread only: it MUST NOT kill a live in-process stream. Cooperative cancellation via run cancellation tokens remains the mechanism for live runs (documented in API docs). A run whose cancel landed mid-flight observes the conflict on its next checkpoint put.

#### Scenario: Cancel interrupted thread
- **WHEN** a host cancels a parked thread whose latest snapshot is Interrupted, with a reason
- **THEN** the thread's latest snapshot becomes Cancelled with the reason recorded and the step history is preserved

#### Scenario: Cancel running parked thread
- **WHEN** a host cancels a thread whose latest snapshot is Running (e.g. process died mid-run)
- **THEN** the thread's latest snapshot becomes Cancelled with the reason recorded

#### Scenario: Cancel is idempotent
- **WHEN** a host cancels a thread that is already Cancelled
- **THEN** the call succeeds without error

#### Scenario: Cancel done thread rejected
- **WHEN** a host cancels a thread whose latest snapshot is Done or Failed
- **THEN** the call fails with code `graph.invalid_cancel` and the snapshot is unchanged

#### Scenario: Cancel racing a live run
- **WHEN** a host cancels a parked thread while another process is actively streaming a run for that thread
- **THEN** the cancel's guarded put wins or loses atomically against the runner's guarded put — the loser receives `checkpoint.conflict` and no snapshot is silently overwritten

#### Scenario: Live stream not killed by cancel
- **WHEN** a run is streaming in-process and the host calls cancel for that thread id
- **THEN** the in-process stream is not aborted by the cancel call itself (it terminates by its own token/completion semantics), and its next guarded checkpoint put fails with `checkpoint.conflict`
