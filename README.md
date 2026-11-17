# accept-crypto-net

[![CI](https://github.com/alena-dev-soft/accept-crypto-net/actions/workflows/ci.yml/badge.svg)](https://github.com/alena-dev-soft/accept-crypto-net/actions/workflows/ci.yml)
AcceptCrypto.NET — a non-custodial USDC payment SDK for ASP.NET Core applications, with invoice management, on-chain confirmation tracking and signed merchant webhooks.

> Status: early development — solution skeleton. See [docs/PROGRESS.md](docs/PROGRESS.md) and [docs/DECISIONS.md](docs/DECISIONS.md).

## Build & test

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build AcceptCrypto.NET.slnx
dotnet test AcceptCrypto.NET.slnx
dotnet run --project src/AcceptCrypto.Service   # Development settings: /health, /scalar (API reference), /v1/*
```

Merchant REST API: see [docs/API.md](docs/API.md) (OpenAPI at `/openapi/v1.json`, UI at `/scalar`).
