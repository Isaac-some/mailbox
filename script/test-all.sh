#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

if ! command -v docker >/dev/null 2>&1; then
    echo "Docker Compose is required for the isolated PostgreSQL tests." >&2
    exit 2
fi

project="mailbox-test-$$"
compose=(docker compose -f docker-compose.test.yml -p "$project")
cleanup() { "${compose[@]}" down --volumes >/dev/null; }
trap cleanup EXIT

"${compose[@]}" up -d --wait
port="$("${compose[@]}" port postgres 5432 | awk -F: 'END { print $NF }')"
if [[ ! "$port" =~ ^[0-9]+$ ]]; then
    echo "Could not determine isolated PostgreSQL port." >&2
    exit 1
fi

export MailArchiverTest__ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=$port;Database=MailArchiverTest;Username=mailtest;Password=mailbox-local-test-only"
dotnet_bin="./.local-tools/dotnet/dotnet"
if [[ ! -x "$dotnet_bin" ]]; then dotnet_bin="dotnet"; fi
"$dotnet_bin" test MailArchiver.sln --configuration Release
node tests/ui/mailbox-reader.mjs
