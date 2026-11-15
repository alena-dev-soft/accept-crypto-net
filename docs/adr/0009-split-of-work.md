# ADR 0009 — Split of work between the author and the coding agent

- **Status:** Proposed
- **Date:** 2026-11-15
- **Spec:** Open question "Split of work"

## Decision

| Area | Author (human) | Agent |
| --- | --- | --- |
| ADRs, status model, public API shape | Decides, reviews, approves | Drafts proposals |
| Domain logic (status machine, money) | Reviews line by line | Implements with exhaustive unit tests |
| Smart contract | Reviews line by line; owns the deployment | Implements + Foundry tests |
| Infrastructure, workers, SDK, demo shop, CI | Reviews PRs | Implements |
| README, article, video, Upwork card | Writes / records | Drafts text, diagrams, screenshots |
| Secrets, deployments, wallets with funds | Only the author | Never |

Process:

- Work happens in small steps on the `dev` branch; every step updates `docs/PROGRESS.md`,
  must leave `dotnet build`/`dotnet test` green, and is handed over via a pull request.
- A PR touching money handling, the status machine, signatures or the contract needs explicit
  author review of the tests first ("do the tests encode the rule from the ADR?").
- Later steps rely on ADRs only after the author has reviewed them; changes to an accepted ADR
  are made by a new ADR that supersedes it.
