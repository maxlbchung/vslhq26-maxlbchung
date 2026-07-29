<!--
Companion artefact referenced by design.md's appendix: the concrete output target for
the per-employee report described in design §5.4.

This file is a verbatim copy of real generated output — `out/reports/employees/
emp-007-amara-sylla.md` from the bundled Northwind Traders engagement — rather than a
hand-written mock-up. Keeping the example a real artefact means the documented format
cannot drift away from what the tool actually produces.

Reproduce it with:
    swarmrt run --attempts 30 --out out
    cat out/reports/employees/emp-007-amara-sylla.md

This persona was chosen because it exercises every section: one approach landed, two were
resisted, and the levers split cleanly into effective and ineffective — so the findings,
vulnerability pattern, and recommendations all have something to say. Personas at 0% or
100% produce a shorter report.
-->

# Individual Awareness Report — Amara Sylla

> **Simulation artefact.** Every target in this report is a fabricated persona at a synthetic company on a reserved, non-routable domain. No real person was contacted, no real system was touched, and no message described here was ever delivered anywhere. Approaches are recorded as *pretext type and tactic only* — this document contains no reusable lure content by design.

## Target profile

| Field | Value |
|---|---|
| Persona ID | `emp-007` |
| Name | Amara Sylla (fabricated) |
| Role | Marketing Manager |
| Department | Marketing |
| Synthetic mailbox | `amara.sylla@northwind-traders.example` (non-routable) |
| Synthetic exposure | `speaks_at_conferences`, `listed_on_public_contact_page`, `collaborates_externally` |
| Engagement | `NWT-2026-07` |

## Attempts against this persona

| Attempt | Pretext type | Tactic | Content safety | Outcome |
|---|---|---|---|---|
| `att-0006` | `recruiter_outreach` | curiosity + reciprocity | cleared | **success** — favorable reply |
| `att-0018` | `shared_document_notification` | curiosity + urgency | cleared | failure — unfavorable reply |
| `att-0030` | `survey_incentive` | curiosity + reciprocity | cleared | failure — unfavorable reply |

1 of 3 delivered attempts produced a favorable reply (33%).

## Findings

### Susceptibilities

- **Recruiter outreach** (curiosity + reciprocity) — Target began the requested action and gave up part of what was asked before hesitating.

### Positive behaviours

- **Verified before acting** (2 attempts): `shared_document_notification`, `survey_incentive`. Target withheld action and asked for confirmation through a channel they already trusted.

## Vulnerability pattern

1 of 3 delivered attempts landed (33%). The levers present in successful attempts were `curiosity` and `reciprocity`. All of them arrived over the chat channel. The dominant persona factor is curiosity about unexpected content, which is high (0.82), set against a low verification habit (0.42) and a moderate training recency (0.55). 1 successful approach was plausible specifically because of this role's synthetic exposure attributes (speaks_at_conferences, listed_on_public_contact_page, collaborates_externally), meaning the pretext did not have to be convincing in general — only convincing for this job. Approaches built on `urgency` did not land, so the gap is specific rather than general susceptibility.

## Recommendations

1. **Recruiter outreach:** Keep career conversations off corporate devices and never open unsolicited recruiter attachments on the corporate network.
2. **Build one verification habit, not a general suspicion.** Pick the two request types this role handles most and make an out-of-band check a required step for both, using contact details already on file rather than any supplied in the request.
3. **Practise the reporting path.** Nothing was escalated to security, so even the attempts this persona did not act on produced no warning for anyone else. One walkthrough of how to report, plus confirmation that a false alarm is welcome, closes that gap.

## Provenance

Derived entirely from `NWT-2026-07.jsonl`. Each row was produced by a separate stateless agent making a single attempt with no knowledge of any other attempt against this persona.

Engine: none (deterministic engine). Responder: rule-weighted personas (deterministic for a given seed). Judgment: marker-based judgment (no model backend).
