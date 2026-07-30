# SERT - Social Engineering Red Teaming

An AI agent workflow that safely simulates phishing-style social-engineering attacks against a fake company to reveal who is most vulnerable, then generates a training report.

## Team

@maxlbchung

## Category

- **Primary:** AI agent/workflow automation
- **Secondary (optional):** Azure OpenAI/LLM app

## What it does

SERT is a defensive security-awareness tool. A central orchestrator dispatches a swarm of agents. Each one makes a **single** simulated social-engineering attempt and reports back if they succeeded or failed.

Every attempt is logged, and the log becomes a training deliverable: which methods worked, against whom, and why. This can help companies better train their employees, and in the recent meta agent fiasco, better guardrail their models too.

[www.404media.co/hackers-simply-asked-meta-ai-to-give-them-access-to-high-profile-instagram-accounts-it-worked](https://www.404media.co/hackers-simply-asked-meta-ai-to-give-them-access-to-high-profile-instagram-accounts-it-worked/)

This project simulates **fabricated** employees at a **made up** company.

## Architecture

```
Orchestrator ──starts a fresh agent──▶ Social-Engineering Agent (one attempt, no memory)
     │                                   ├─ write one fake message
     │                                   ├─ safety check ──UNSAFE──▶ blocked
     │                                   ├─ send to the fake employee
     │                                   ├─ read the reply
     │                                   ├─ decide: success or failure
     │                                   └─ agent discarded, message wiped from memory
     ◀──────one result───────────────────┘
     └─ save result to log ──▶ (after all attempts) ──▶ report generator
```

Only the orchestrator keeps state and writes files. Agents just return results; the orchestrator
saves them. That separation is what makes the log a trustworthy record — no agent can edit or hide
its own result.

## Tech stack

- Languages: C#
- Frameworks/libraries: .NET 9 (no third-party dependencies)
- AI models/services: Azure OpenAI or any OpenAI-compatible chat endpoint (GitHub Models used by default). A model backend is required — the swarm and the target persona are both model agents
- Hosting: Runs locally

## Getting started

### Prerequisites

- .NET 9 SDK
- Required: an API key for Azure OpenAI or GitHub Models, set in the `SWARMRT_API_KEY` environment variable (a GitHub PAT with Models access works)

### Setup

```bash
# Clone the repo
git clone https://github.com/maxlbchung/vslhq26-maxlbchung.git
cd vslhq26-maxlbchung

dotnet build

# Preview what an engagement would do, without sending anything
dotnet run --project src/SwarmRT -- plan --attempts 12

# Run one engagement end to end
dotnet run --project src/SwarmRT -- run --attempts 30

# Rebuild the reports from an existing log
dotnet run --project src/SwarmRT -- report --log out/NWT-2026-07.jsonl

dotnet test
```

### Configuration

```powershell
# Windows, persists for the user; open a new terminal afterwards
[Environment]::SetEnvironmentVariable("SWARMRT_API_KEY", "<token>", "User")
```

```bash
# macOS / Linux
export SWARMRT_API_KEY=<token>
```

Keys are read from `SWARMRT_API_KEY`, then `GITHUB_MODELS_TOKEN`, `GITHUB_TOKEN`, `GH_TOKEN`.
Azure OpenAI or any other OpenAI-compatible endpoint works via `--endpoint`, `--model`, and `--key-env`.
**A model backend is required; a run with no key configured is an error.**

## Demo (required)

- Video file in this repo: [`./demo/demo.mp4`](./demo/demo.mp4)

## Known limitations

By design, SERT does not:

- send anything through real channels (email, SMS, or voice) — delivery is always simulated
- hold multi-message conversations or adapt mid-attack; each agent makes one attempt and cannot follow up
- remember anything between attempts or coordinate across them
- gather information on real people or companies
- run in the cloud

## License

This project: MIT.

### Third-party licenses & attributions

**Runtime dependencies:** none beyond the .NET 9 base class library (MIT).

**Test-only dependencies** (not shipped with the tool):

| Package | Version | License |
| --- | --- | --- |
| xunit | 2.9.2 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.12.0 | MIT |
| coverlet.collector | 6.0.2 | MIT |

**AI models/services:** used via API, not redistributed — Azure OpenAI and GitHub Models are subject to their respective provider terms. No model weights are bundled.

**Assets:** the only data asset, `src/SwarmRT/data/synthetic-org.json`, is fully fabricated original content (made-up company and employees) authored for this project. No third-party datasets, images, fonts, or audio are used.
