# SwarmRT

**A stateless agent-swarm social-engineering simulator with orchestrator logging and report generation.**

SwarmRT is a defensive security-awareness tool. A central orchestrator dispatches a swarm of
stateless agents; each one makes a **single** simulated social-engineering attempt against a
**fabricated** employee at a **synthetic** company, returns one result object, and is discarded.
Every attempt is logged, and the log becomes a training deliverable: which pretexts worked,
against whom, and why.

Nothing is ever delivered to a real person, mailbox, or system.

> Implementation of [`design.md`](design.md). Section references throughout the code and this
> document point back to it.

---

## What it does

```
swarmrt run --attempts 30
```

```
  engagement    NWT-2026-07
  synthetic org Northwind Traders (northwind-traders.example), 12 personas
  plan          30 attack attempts + 5 safety control tests
  ...
[  1/35] att-0001  Liam Whitfield     executive_authority_request    SUCCESS
[  2/35] att-0002  Hannah Cole        shared_document_notification   failure
...
[ 31/35] att-0031  Priya Raman        control:routable-host          BLOCKED

  attack attempts   30  (success 11, failure 19, blocked 0)
  success rate      37% of 30 delivered
  control tests     5/5 blocked pre-delivery
  most susceptible  Liam Whitfield (Executive Assistant to the CEO) — 3/3 landed
  top pretext       hr_benefits_notice — 3/5 landed
```

Produces:

| Artefact | What it is |
|---|---|
| `out/{engagement}.jsonl` | Append-only log, one result object per line, verbatim per design §5.3 |
| `out/{engagement}.run.json` | Run manifest: engine used, hash chain, failed attempts, usage |
| `out/reports/org-summary.md` | Org-wide report: tallies, pretext and lever breakdowns, susceptibility ranking, recommendations |
| `out/reports/employees/*.md` | One individual report per tested persona ([example](social-engineering-report-example.md)) |

---

## Quick start

Requires the .NET 9 SDK. No other dependencies — the tool itself references no NuGet packages.

```bash
dotnet build

# See what an engagement would do, without executing anything
dotnet run --project src/SwarmRT -- plan --attempts 12

# Run one end to end
dotnet run --project src/SwarmRT -- run --attempts 30

# Rebuild the reports from the log alone
dotnet run --project src/SwarmRT -- report --log out/NWT-2026-07.jsonl

dotnet test
```

### Using a model backend

Agents are real model calls when a key is present. Create a fine-grained GitHub PAT with
**Models: Read-only** — that is the only permission needed; the tool never touches repositories.

```powershell
# Windows, persists for the user; open a new terminal afterwards
[Environment]::SetEnvironmentVariable("SWARMRT_API_KEY", "<token>", "User")

dotnet run --project src/SwarmRT -- run --engine llm --attempts 20 --responder llm --narrative
```

```bash
# macOS / Linux
export SWARMRT_API_KEY=<token>
```

Keys are read from `SWARMRT_API_KEY`, then `GITHUB_MODELS_TOKEN`, `GITHUB_TOKEN`, `GH_TOKEN`.
**Prefer `SWARMRT_API_KEY`**: if you have ever run `gh auth login`, a `GITHUB_TOKEN` may already
exist without the Models permission, and it would win the lookup and produce a confusing 401.

Verify the credential independently before blaming the tool:

```powershell
Invoke-RestMethod -Method Post -Uri "https://models.github.ai/inference/chat/completions" -Headers @{ Authorization = "Bearer $env:SWARMRT_API_KEY" } -ContentType "application/json" -Body '{"model":"openai/gpt-4o-mini","messages":[{"role":"user","content":"say ok"}]}'
```

A `choices` array back means the key is good. `401` means it is wrong or lacks Models permission;
`403` usually means Models is not enabled for the account or organisation yet.

Any OpenAI-compatible endpoint works via `--endpoint`, `--model`, and `--key-env`.

**Without a key the tool still runs end to end** on a deterministic engine (template lures,
rule-weighted personas, marker-based judgment). That output is never presented as model output:
the console, the run manifest, and every report name the engine that actually produced the
engagement. Use `--engine llm` to require a backend rather than accept the fallback.

---

## How it works

```
Orchestrator ──spawn fresh agent──▶ Engineering Agent (stateless, one attempt)
     │                                   ├─ compose one lure
     │                                   ├─ content-safety gate ──FLAGGED──▶ blocked
     │                                   ├─ deliver to synthetic employee
     │                                   ├─ receive one reply
     │                                   ├─ judge it ──▶ success | failure
     │                                   └─ instance discarded, lure buffer zeroed
     ◀──────one result object────────────┘
     └─ append to JSONL log ──▶ (after all attempts) ──▶ report generator
```

The orchestrator is the only stateful component and the only thing that writes to disk. Agents
return data; the orchestrator persists it. That split is what makes the log an audit trail
rather than a self-report — no agent can write, amend, or suppress its own row.

### Statelessness is structural, not aspirational

Design §2's "clone-and-wipe" is enforced by the types, not by convention:

- `AgentDefinition` holds collaborators only — no per-attempt data — so a clone carries nothing.
- `EngineeringAgent.RunAsync` throws on a second call. "One attempt only" cannot be violated by
  a careless caller.
- The only per-attempt state is the composed lure, held in a `char[]` that `Dispose` zeroes.
  `ComposedLure.Reveal()` throws after disposal, so use-after-wipe is a crash rather than a
  silent read of a stale buffer.
- Each backend call sends exactly one system and one user message. Statelessness is a property
  of the transport; there is no history to forget.

### The judgment is a real classification

Design §6 has the agent decide whether a reply was favorable. The judge is handed **only the
reply text** — never the responder's internal decision. With the rule-weighted responder, that
decision is kept in a side channel the orchestrator reads *afterwards*, purely to report how
often the two views agreed. The manifest and reports carry that agreement rate, and the report
states plainly that on the deterministic engine both sides are template-driven, so near-total
agreement is a pipeline consistency check rather than independent corroboration.

---

## Safety

Design §8's guardrails are implemented as mechanisms rather than intentions.

**Synthetic-only targets (§8.1).** Rosters may only address domains the IETF has reserved as
permanently unresolvable (`.example`, `.invalid`, `.test`, `.localhost`, `example.com/net/org`).
A roster with a routable mailbox aborts the run with exit code 3 — it is not a warning. The check
runs at load *and* again in the orchestrator, immediately before anything is "delivered".

**Content-safety gate on every lure, pre-delivery (§8.2).** Two layers:

- *Deterministic heuristics*, always first. Free, offline, and unpersuadable by a clever prompt.
  Enforces the simulation label, the stub length limit, and the absence of routable hosts,
  real addresses or numbers, impersonated brands, tradecraft, and working capture mechanics.
- *Model self-check*, when a backend is configured. Covers the semantic harm categories a regex
  cannot see. **Fails closed**: an unavailable or unparseable check blocks the lure.

**Pretext-level recording (§8.3).** The lure text exists only in memory. Only `attempt_summary`
— a pretext-level description — reaches disk. This is enforced by a test that plants a canary
string in every lure and asserts it appears in no file the tool writes.

**Nothing model-authored reaches disk unscreened.** Attempt summaries, success and failure
reasons, and gate rationales are screened on their way into the log and replaced with a safe
equivalent if they carry an identifier, a brand, or a capture mechanic. Replacements are counted
in the manifest and disclosed in the report rather than hidden.

**Watermarking (§8.4).** Every simulated lure carries `[SIMULATED]`; replies carry
`[SIMULATED REPLY]`. Unlabelled content is blocked by the gate before it can reach a responder.

**Audit trail (§8.5).** The log is opened append-only and never seeked. The logger maintains a
rolling SHA-256 chain over the bytes written and records the digest in the manifest, so a later
edit to the log is detectable without adding fields to the log contract that design §5.3 fixes.
`swarmrt report` verifies the chain before building anything.

### The gate is proven, not asserted

Every engagement submits five fixed, hand-written known-bad inputs through the *identical* path
as a real attempt — same agent, same gate, same logger — each targeting a different rule. They
appear in the log as `blocked` rows tagged `control_test_prohibited_lure`, and the report gives
them their own section, excluded from susceptibility statistics.

No model is ever asked to generate prohibited content; the inputs are literal constants in
[`SafetyProbe.cs`](src/SwarmRT/Safety/SafetyProbe.cs). If any control test is *not* blocked, the
run exits non-zero and the report says the gate must be treated as unverified.

---

## Out of scope

Deliberately excluded per design §9: any real delivery channel, multi-turn or adaptive attack
behaviour, persistent agent memory or cross-attempt coordination, recon against real sources, a
live UI, and cloud deployment. Agents make one attempt and cannot follow up.

Lures are simulation stubs by construction — short, abstract, watermarked, and gated. The tool
is not a phishing-content generator and the gate actively prevents it from becoming one.

---

## Command reference

```
swarmrt run     [options]      Run an engagement, log it, generate reports
swarmrt report  --log <path>   Rebuild reports from an existing log
swarmrt plan    [options]      Print the attempt plan without executing it
swarmrt help                   Full option list
```

Selected `run` options:

| Option | Default | Notes |
|---|---|---|
| `--attempts <n>` | 24 | Capped at the roster's unique persona × pretext pairs |
| `--engine <mode>` | `auto` | `auto` \| `llm` \| `deterministic` |
| `--responder <mode>` | `rules` | `rules` (reproducible) \| `llm` (varied) |
| `--seed <n>` | 20260729 | Same seed reproduces the plan and persona jitter exactly |
| `--rpm <n>` | 10 | Request pacing; see the rate-limit note below |
| `--concurrency <n>` | 1 | Only a sequential run guarantees log order matches plan order |
| `--narrative` | off | Adds a model-written summary paragraph to the org report |
| `--no-safety-probe` | on | Skips the in-band control tests |
| `--overwrite` | off | Required to replace an existing log for the same engagement id |

Exit codes: `0` success · `1` run error · `2` usage error · `3` roster failed the
synthetic-only check.

**Rate limits.** GitHub Models' free tier caps throughput (~10–15 req/min, concurrency 2). With
the LLM engine each attempt costs 3 backend calls (4 with `--responder llm`), so 30 attempts is
roughly 90–120 calls. The throttle paces requests, honours `Retry-After`, and pushes the whole
schedule back on a 429 so every waiter backs off rather than just the refused call. The run
header prints an up-front estimate.

---

## Layout

```
src/SwarmRT/
  Agents/          Stateless agent, lure composers, reply judges, the scrubbable lure buffer
  Cli/             Argument parsing and the run / report / plan verbs
  Contracts/       Design §5 data contracts and their invariants
  Logging/         Append-only JSONL logger, reader, hash chain
  Model/           IModelClient seam, OpenAI-compatible client, throttle, JSON recovery
  Orchestration/   Orchestrator, attempt planner, run manifest
  Org/             Synthetic roster, persona traits, pretext taxonomy, the synthetic-only guard
  Reporting/       Aggregation, Markdown report generation, optional narrative
  Responders/      Rule-weighted and model-driven synthetic employees
  data/            The fabricated Northwind Traders roster
tests/SwarmRT.Tests/   177 tests
```

### Adding a roster

Copy `src/SwarmRT/data/synthetic-org.json`, keep `"synthetic": true`, and keep every mailbox on
a reserved domain — the guard will reject anything else. Persona traits are 0.0–1.0 dials; the
four susceptibility dials (authority deference, urgency, curiosity, helpfulness) are what
pretexts score against, and the three resistance dials (technical literacy, verification habit,
training recency) push back. Pass it with `--org`.

---

## Known limitations

- **The deterministic engine is a self-consistent simulation, not a measurement.** Template
  lures, rule-weighted personas, and a marker-based judge agree with each other by construction.
  It exists so the pipeline runs and demos reliably without a token; the LLM engine is where the
  agent behaviour is real. Every artefact labels which one ran.
- **Success rates come from a fabricated persona model,** calibrated to sit in the range
  published phishing-simulation studies report. They describe the model, not any real
  population, and the roster is deliberately skewed toward susceptible personas.
- **Concurrency above 1 makes log order completion order** rather than plan order. Each row is
  still written exactly once, and timestamps remain authoritative.
- **The heuristic gate's brand list is finite.** It catches the commonly impersonated names, and
  the model self-check covers the rest when a backend is configured.
