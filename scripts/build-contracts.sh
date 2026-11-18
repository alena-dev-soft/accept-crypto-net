#!/usr/bin/env bash
# Rebuilds the test-only MockUsdc contract and refreshes the committed artifact
# (src/AcceptCrypto.LocalChain/Contracts/MockUsdc.json: ABI + creation bytecode).
# Only needed when contracts/src/MockUsdc.sol changes. Requires Foundry (forge) and jq.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root/contracts"
forge build --force --silent
jq '{contractName: "MockUsdc", compiler: "solc 0.8.24 (optimizer 200, evm cancun, no metadata hash)", abi: .abi, bytecode: .bytecode.object}' \
  out/MockUsdc.sol/MockUsdc.json > "$root/src/AcceptCrypto.LocalChain/Contracts/MockUsdc.json"
echo "Wrote src/AcceptCrypto.LocalChain/Contracts/MockUsdc.json"
