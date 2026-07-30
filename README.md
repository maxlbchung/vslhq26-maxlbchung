# SERT - Social Engineering Red Teaming

An AI agent workflow that safely simulates phishing-style social-engineering attacks against a fake company to reveal who is most vulnerable, then generates a training report.

## Team

@maxlbchung

## Category

- **Primary:** AI agent/workflow automation
- **Secondary (optional):** Azure OpenAI/LLM app

## What it does

SERT is a defensive security-awareness tool. A coordinator (the "orchestrator") sends out many one-shot AI agents.
Each agent runs a single simulated social-engineering attempt — a phishing-style message — against a fake employee, then reports whether it worked.
The orchestrator keeps trying new agents with different tactics to uncover more weaknesses.
Everything runs against a **made-up** company with **fabricated** employees; nothing is ever sent to a real person, mailbox, or system.

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
- AI models/services: Azure OpenAI or any OpenAI-compatible chat endpoint (GitHub Models used by default); a built-in offline engine runs when no key is set
- Hosting: Runs locally

## Getting started

### Prerequisites

- .NET 9 SDK
- Optional, only for real AI calls: an API key for Azure OpenAI or GitHub Models, set in the `SWARMRT_API_KEY` environment variable

### Setup

```bash
# Clone the repo
git clone https://github.com/<owner>/<repo>.git
cd <repo>

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
**Without a key the tool still runs end to end** using the built-in offline engine.

## Demo (required)

- Video file in this repo (preferred): `./demo/demo.mp4` (or similar path)
- Video link (YouTube, Loom, etc.) if not committed to repo:
- Deployed URL (if any):

## Known limitations

By design, SERT does not:

- send anything through real channels (email, SMS, or voice) — delivery is always simulated
- hold multi-message conversations or adapt mid-attack; each agent makes one attempt and cannot follow up
- remember anything between attempts or coordinate across them
- gather information on real people or companies
- run in the cloud

## License

MIT (or your choice)
