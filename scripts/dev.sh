#!/usr/bin/env bash
# Starts the AcceptCrypto.NET service for Tree Work Preview (`npm run dev`).
# Installs the .NET 10 SDK into ~/.dotnet if no .NET 10 SDK is available.
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^10\.'; then
  echo "Installing .NET 10 SDK into \$HOME/.dotnet ..."
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet" >/dev/null
  export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
fi

PORT="${PORT:-8080}"
exec dotnet run --project src/AcceptCrypto.Service --no-launch-profile --urls "http://0.0.0.0:${PORT}"
