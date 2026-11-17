#!/usr/bin/env bash
# Starts a local Anvil chain (needs Foundry: https://getfoundry.sh) and prepares it for the stack:
# deploys the TEST-ONLY mock USDC at 0x5FbDB2315678afecb367f032d93F642f64180aa3 and mints 1,000,000 USDC
# to the pre-funded payer (Anvil account #1). Ctrl+C stops Anvil.
# Without Foundry: run `docker run -p 8545:8545 ghcr.io/foundry-rs/foundry:latest "anvil --host 0.0.0.0"`
# and then `dotnet run --project src/AcceptCrypto.Payer -- setup`.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
port="${ANVIL_PORT:-8545}"
export PATH="$PATH:$HOME/.foundry/bin"

anvil --port "$port" --silent &
anvil_pid=$!
trap 'kill $anvil_pid 2>/dev/null || true' EXIT INT TERM

export ACCEPTCRYPTO_RPC_URL="http://localhost:$port"
for _ in $(seq 1 50); do
  if curl -s -X POST -H 'content-type: application/json' --data '{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}' "$ACCEPTCRYPTO_RPC_URL" >/dev/null; then
    break
  fi
  sleep 0.2
done

dotnet run --project "$root/src/AcceptCrypto.Payer" -- setup
echo "Anvil is running on $ACCEPTCRYPTO_RPC_URL (chain 31337). Press Ctrl+C to stop."
wait $anvil_pid
