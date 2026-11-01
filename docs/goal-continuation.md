# Goal continuation

Goal automation reuses the existing conversation, run coordinator, SDK retry policy and context compaction.
It does not introduce another agent loop or retry transport.

## Configuration and accounting

The existing Hub preferences JSON accepts these properties (restart Hub after editing it):

| Property | Default | Meaning |
| --- | ---: | --- |
| `GoalContinuationLimit` | `0` | Maximum automatic follow-up runs; `0` removes this ceiling only. |
| `GoalTokenBudget` | `1000000` | Goal-wide reported plus estimated tokens; `0` disables this ceiling only. |
| `ProviderMaxRetries` | `2` | SDK retries per request, clamped to 0–10. Not goal continuations. |
| `GoalNoProgressLimit` | `3` | Consecutive unproductive requests/runs before pausing, clamped to 2–20. |
| `GoalCooldownLimit` | `2` | Recoverable provider cooldowns before pausing, clamped to 0–5. |

Continuation and token limits are copied into a new goal's conversation. Older sessions with missing
limits inherit preferences when next used; their transcript, continuation count and paused state are
preserved. Historical token consumption is not reconstructed. This change adds configuration fields,
not a new settings page.

The initial run does not consume a continuation. A provider retry or cooldown recovery resumes the
same logical run and does not increment continuation or no-progress counters. All goal model requests,
including tool-loop follow-ups and summaries, reserve estimated input/schema/output tokens before
sending. Reported usage replaces that request's reservation. Missing usage and interrupted responses
without usage retain an explicit estimate, not a claim of exact billing. SDK attempts without usage
cannot be individually billed by this ledger. Budget admission is consequently a conservative estimate,
not an exact invoice or a guarantee that a provider's tokenizer matches Hub's estimate.

## Stops and recovery

- **Active**: may request the model, subject to each independent guard.
- **Waiting**: provider cooldown, pending decision, or child result. No periodic model polling.
  Provider wake time is persisted; opening a waiting conversation restores its timer. Existing decision
  and child-result delivery paths resume other waits.
- **Paused**: user stop, no progress, or exhausted cooldown allowance. Resume is explicit.
- **Blocked**: model-reported prerequisite, exhausted quota, or authentication failure. Fix the cause
  before resuming.
- **Complete**: completion marker accepted, or explicitly completed by the person.
- **BudgetLimited**: token budget or continuation ceiling reached. The goal capsule offers a budget
  increase and then explicit resume; it does not silently start another request.

Successful tool calls alone are not progress. Normalized repeated evidence, failed commands and
no-op writes do not reset the progress guard. A bounded fingerprint history survives continuations and
compaction. Existing exact-call loop protection shares normalization/result classification with the
goal guard. The checkpoint and ledger stay on the conversation, alongside the existing summary.

Recoverable 408/429/500/502/503/504 and transport failures use bounded SDK retries with exponential
backoff and jitter. Valid `Retry-After` (seconds or HTTP date) and millisecond variants override jitter.
Long waits park goal automation; exhausted quota and authentication failures are not retried. Pause,
clear, delete and workspace disposal cancel pending goal wakes.

## Completion markers

`[GOAL_COMPLETE]` and `[GOAL_BLOCKED]` are recognized only as the final standalone line outside a
code fence or quote. Only that control line is removed from displayed prose. A completion claim cannot
win over a pending approval, unfinished plan, outstanding child result or the current goal's latest
known failed command. Plan checkboxes alone are not verification. Accepted model claims are shown as
**Model reported complete (unverified)**; explicit user completion is authoritative.

## Regression checks

- `--check-ai-sessions`: limits, legacy defaults, evidence deduplication, markers and persistence.
- `--check-ai-context`: request-level budget admission inside tool invocation and usage settlement.
- `--check-ai-providers`: retry classification, quota/auth rejection, cancellation, jitter, server delay,
  network recovery and long-wait handoff using deterministic local transports.
- `--verify-shell`: real coordinator flow for zero-limit no-progress stopping, budget refusal/resume,
  cooldown persistence/restart, and incomplete dependency rejection. No live provider is required.
