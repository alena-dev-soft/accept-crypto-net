# Packaging drafts (Upwork, dev.to)

Drafts for the "Packaging for Upwork" part of the spec. **Written from the repository contents only** — no facts
about the author, no client numbers, no prices. Everything in `[SQUARE BRACKETS]` is for the author to fill in or
decide. Review, adjust the voice, then publish.

| File | What | Status |
| --- | --- | --- |
| [upwork-portfolio-card.md](upwork-portfolio-card.md) | Upwork portfolio item: problem → solution → result, skills, what to screenshot | draft |
| [devto-article.md](devto-article.md) | dev.to article "Accepting USDC in a .NET app": structure + full draft | draft |
| [project-catalog-offer.md](project-catalog-offer.md) | Upwork Project Catalog offer "Add crypto (USDC) payments to your .NET app": description, what's included, packages with placeholders | draft |

Related: the 1–2 minute video script is in [../testnet.md](../testnet.md#recording-the-12-minute-video); existing
images in [../images](../images) (payment cycle GIF, payment page states, demo shop screens, Base Sepolia payment page).

Facts the drafts rely on (check them again before publishing if the code changed):

- Repository: `https://github.com/alena-dev-soft/accept-crypto-net` (from the README badge).
- Stack: .NET 10, ASP.NET Core, PostgreSQL (EF Core), transactional outbox + `BackgroundService` workers, Nethereum
  (HD derivation / local-chain tooling), Anvil + mock 6-decimal USDC locally, Base Sepolia + Circle test USDC on testnet.
- SDK package id `AcceptCrypto.NET`, version `0.1.0-alpha`, **not published on nuget.org yet** (README).
- No mainnet, no real money: the drafts say so wherever the project is described.
