# SwarmRT — Design Document

**Project:** Stateless agent-swarm social-engineering simulator with orchestrator logging and report generation
**Target event:** VSLive! Microsoft AI Hackathon 2026
**Primary category:** Best AI Agent or Workflow Automation
**Stack:** .NET console orchestrator · GitHub Models (GPT-4o-mini) · OpenAI-compatible API

---

## 1. Overview

SwarmRT is a defensive security-awareness tool. A central **orchestrator** dispatches a **swarm of stateless engineering agents**, each of which makes a **single social-engineering attempt** against a **synthetic employee**. If an agent receives a favorable reply, it reports the success and the reason back to the orchestrator. The orchestrator **logs every attempt** (success or failure) to a file, then **generates a report** for the organization being tested — both an org-wide summary and per-employee individual reports.

The tool produces a training deliverable: which pretexts worked, against whom, and why. It does not attack any real system.

### What it is
- A red-team **simulation** run entirely against a fabricated (synthetic) organization.
- A generator of **awareness reports** describing outcomes and recommendations.

### What it is not
- Not a tool that targets real people, real inboxes, or real credentials.
- Not a phishing-content generator; lures are recorded at the level of **pretext type and tactic**, not as ready-to-send copy.

---

## 2. Core Design Principle — Stateless Clone-and-Wipe

There is **one** engineering-agent definition. For every attempt, the orchestrator instantiates a **fresh copy** of it with **no carried-over memory or context**, runs a single attempt, collects the result, and **discards** the instance.

Consequences of this choice:
- **Minimal engineering.** No per-agent state, no memory store, no conversation history to manage.
- **Independent attempts.** Each attempt is isolated; nothing leaks between them.
- **Trivially parallel in principle.** Because instances share no state, attempts *could* run concurrently — but see the rate-limit note below.
- **Deterministic logging.** Each instance produces exactly one result object, which maps to exactly one log line.

"Memory wipe" is implemented simply as: **a single stateless chat-completion call per attempt, seeded only with that attempt's assignment.** Chat-completion calls are stateless by default — no history is carried unless deliberately passed — so statelessness is free. There is no agent runtime, no session, and nothing to erase because nothing persists. An "agent" here is one API call.

> **Rate-limit note:** GitHub Models' free tier caps throughput (~10–15 req/min, concurrency 2). Run attempts **sequentially** for the demo and size the run to ~20–40 attempts. True parallelism is available only if you switch to a paid backend.

---

## 3. Components

### 3.1 Orchestrator
The single stateful component. Responsibilities:
1. Load the synthetic org and the attempt plan (which pretext types to try against which employees).
2. For each planned attempt: spawn a fresh engineering agent, hand it its assignment, await its result object.
3. Append each result to the log file.
4. On completion, invoke the report generator.

The orchestrator is the **only** component that writes to disk. Agents return data; the orchestrator persists it. This separation is the audit guarantee.

### 3.2 Engineering Agent (the clonable unit)
A stateless agent with a fixed definition. Given one assignment (target + pretext type + tactic), it:
1. Composes a single lure for that pretext.
2. Submits the lure to the content-safety gate.
3. If cleared, "delivers" it to the synthetic employee and receives one reply.
4. Judges whether the reply is **favorable** (target took the bait).
5. Returns a single **result object** and terminates.

It makes **one attempt only**. It does not retry, adapt, or follow up.

### 3.3 Synthetic Organization
A fabricated company: an employee roster (id, name, role, department) and any synthetic exposure attributes (e.g., "listed on public contact page"). Held in a simple in-memory structure or flat file. No real data.

### 3.4 Synthetic Employee Responder
Given a delivered lure and a target employee, returns a single reply representing that employee's reaction. Two implementation options (see §7): rule-weighted per persona (reliable) or LLM-driven per persona (more impressive). Reply is what the engineering agent judges as favorable/unfavorable.

### 3.5 Content-Safety Gate
Every composed lure passes through Azure AI Content Safety **before** simulated delivery. Flagged lures are blocked, never "delivered," and logged with outcome `blocked`. This is both a real guardrail and a demonstrable control.

### 3.6 Logger
An append-only writer owned by the orchestrator. One line per attempt, JSONL format. This file is the single source of truth for reporting.

### 3.7 Report Generator
Reads the JSONL log and emits: (a) an org-wide summary report, and (b) one individual report per employee. Pure aggregation — no model calls required, though a model may be used to phrase the narrative findings.

---

## 4. Execution Flow

For each attempt in the plan:

```
Orchestrator
  ├─ spawn fresh engineering agent (blank memory) with assignment
  │       { target_employee, pretext_type, tactic }
  │
Engineering Agent (stateless, single attempt)
  ├─ compose one lure
  ├─ content-safety gate
  │     ├─ FLAGGED → return { outcome: "blocked", reason }  ─┐
  │     └─ CLEARED ↓                                          │
  ├─ deliver lure to Synthetic Employee Responder             │
  ├─ receive one reply                                        │
  ├─ judge reply                                              │
  │     ├─ FAVORABLE   → return { outcome: "success", success_reason }
  │     └─ UNFAVORABLE → return { outcome: "failure", failure_reason }
  │
  └─ (instance discarded — memory wiped by disposal) ─────────┘
Orchestrator
  └─ append result object to log file (JSONL)

After all attempts:
Orchestrator
  └─ invoke Report Generator → org report + per-employee reports
```

---

## 5. Data Contracts

### 5.1 Agent Assignment (Orchestrator → Agent)
```json
{
  "engagement_id": "NWT-2026-07",
  "attempt_id": "att-0007",
  "target_employee_id": "emp-004",
  "pretext_type": "it_helpdesk_impersonation",
  "tactic": "urgency + authority"
}
```

### 5.2 Result Object (Agent → Orchestrator)
Returned once per attempt. Exactly one of `success_reason` / `failure_reason` is populated (null for `blocked`).
```json
{
  "attempt_id": "att-0007",
  "engagement_id": "NWT-2026-07",
  "timestamp": "2026-07-29T18:42:11Z",
  "target_employee_id": "emp-004",
  "pretext_type": "it_helpdesk_impersonation",
  "tactic": "urgency + authority",
  "content_safety_flagged": false,
  "outcome": "success",
  "success_reason": "Target replied agreeing to re-enroll credentials before the stated deadline without verifying the sender.",
  "failure_reason": null,
  "attempt_summary": "Posed as IT requiring MFA re-enrollment before end of day."
}
```

**`outcome` enum:** `success` · `failure` · `blocked`

- `success` → favorable reply received; `success_reason` explains what made it work.
- `failure` → reply was unfavorable (ignored, refused, or reported); `failure_reason` explains why it didn't land.
- `blocked` → lure flagged by content-safety gate; never delivered.

### 5.3 Log Line (JSONL)
The result object is written verbatim as one line to `{engagement_id}.jsonl`. The log file is append-only; the orchestrator never rewrites prior lines.

### 5.4 Report Structure
Generated from the log:

**Org-wide summary**
- Engagement metadata (id, synthetic org, window).
- Aggregate tally: counts of success / failure / blocked.
- Breakdown by `pretext_type` and by `tactic` (which pretexts landed most).
- Ranked list of most-susceptible employees.

**Per-employee report** (see companion example artifact)
- Target profile.
- Attempt table: pretext type, tactic, content-safety result, outcome.
- Findings (susceptibilities and positive behaviors).
- Vulnerability pattern.
- Recommendations.

---

## 6. "Favorable Reply" Judgment

The engineering agent, after receiving the single reply, classifies it as favorable or not. A reply is **favorable** when the synthetic employee's response indicates they took the intended action or committed to it (e.g., agreed to reset credentials, provided requested info, clicked the simulated link). It is **unfavorable** when the employee ignores, refuses, questions, or reports the lure.

The `success_reason` / `failure_reason` string is generated by the agent from the reply — a short natural-language explanation of *why* the attempt did or didn't work. This reason is what makes the final report useful, so it is a required field on non-blocked outcomes.

The judgment is made in the **same single agent turn** — no second round-trip, no follow-up message. This preserves the one-attempt, stateless design.

---

## 7. Tech Stack (scoped)

| Concern | Choice | Notes |
|---|---|---|
| Orchestration | **.NET console app** | Plain loop over the attempt plan. No agent framework — each attempt is one API call. |
| Model backend | **GitHub Models** (`https://models.github.ai/inference`), model `openai/gpt-4o-mini`, auth via GitHub PAT | OpenAI-compatible; keeps Microsoft-ecosystem fit with no Azure provisioning. |
| HTTP + parsing | `HttpClient` + `System.Text.Json` | Standard OpenAI-shaped request/response; no SDK required. |
| Model-call abstraction | Single `CallModel(prompt) → json` method | Lets you flip GitHub Models → Claude API if rate limits bite mid-demo. |
| "Agent" instantiation | Fresh stateless call per attempt | "Clone-and-wipe" is automatic — no history is passed. |
| Structured output | Prompt for strict JSON; parse into the result object | Request the result-object schema explicitly; validate on parse. |
| Safety gate | LLM self-check call, **or** OpenAI moderation endpoint | Produces the `blocked` outcome; self-check keeps it single-backend. |
| Synthetic org | In-memory or flat JSON | Fabricated roster; no real data. |
| Employee responder | Rule-weighted per persona (recommended) or LLM-driven | Rules are demo-reliable; one LLM persona optional to show it's possible. |
| Logging | Append-only JSONL, orchestrator-owned | Single source of truth. |
| Report generator | Reads JSONL → Markdown | Pure aggregation; optional model call for narrative phrasing. |

**Backend access:** the one external dependency is a **GitHub PAT** with Models access. Generate it and confirm a single test call works *before* night 1 — if the token works, nothing else can block you on the model side.

---

## 8. Safety Guardrails (built in)

1. **Synthetic-only targets.** Hardcoded fabricated org; no real domains, accounts, or PII.
2. **Content-safety gate on every lure**, pre-delivery; flagged content is blocked and logged.
3. **Pretext-level recording.** Logs and reports capture pretext *type and tactic*, not deployable lure copy.
4. **Watermark/label** all generated content as simulation.
5. **Orchestrator-only disk writes**, giving a complete, tamper-evident audit trail of every attempt.

These guardrails are also the demo's safety narrative: state them once up front, then let the artifacts show them working (especially a live `blocked` outcome).

---

## 9. Out of Scope (explicitly excluded)

To keep the build focused, the following are **not** part of this design:
- Live three-panel / real-time UI, SignalR streaming, Blazor/React front end.
- Any multi-turn or adaptive attack behavior (agents make one attempt only).
- Persistent agent memory, learning, or cross-attempt coordination.
- Real delivery channels (email/SMS/voice) — delivery is simulated to the synthetic responder.
- Cloud hosting/deployment — runs locally for the demo.
- Recon/OSINT automation against real sources.

---

## Appendix — Companion Artifact

The example individual report (`social-engineering-report-example.md`) is the concrete output target for §5.4's per-employee report and demonstrates the intended tone, safety framing, and findings structure.