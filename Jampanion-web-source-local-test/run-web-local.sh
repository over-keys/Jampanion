#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PORT="${1:-5279}"
WEB_PROJECT="$ROOT/src/Jampanion.Web/Jampanion.Web.csproj"
CORE_PROJECT="$ROOT/src/Jampanion.Core/Jampanion.Core.csproj"
WEB_DIR="$ROOT/src/Jampanion.Web"
SOUNDFONT="$WEB_DIR/wwwroot/soundfonts/FluidR3_Jampanion.sf3"
EXPECTED_SHA256="2e4aa17f20743930c87ada7cc1fee2228ecd2bb0e2de75a83cd590c53bcd0d63"

fail() {
  printf 'Error: %s\n' "$1" >&2
  exit 1
}

command -v dotnet >/dev/null 2>&1 || fail '.NET SDK 10 is required.'
command -v node >/dev/null 2>&1 || fail 'Node.js 20 or later is required.'
command -v npm >/dev/null 2>&1 || fail 'npm is required.'
[[ -f "$CORE_PROJECT" ]] || fail 'Extract this source bundle into the root of the Jampanion repository; Jampanion.Core was not found.'
[[ -f "$WEB_PROJECT" ]] || fail 'Jampanion.Web.csproj was not found.'
[[ -s "$SOUNDFONT" ]] || fail 'The bundled SoundFont is missing.'

if command -v sha256sum >/dev/null 2>&1; then
  ACTUAL_SHA256="$(sha256sum "$SOUNDFONT" | awk '{print $1}')"
elif command -v shasum >/dev/null 2>&1; then
  ACTUAL_SHA256="$(shasum -a 256 "$SOUNDFONT" | awk '{print $1}')"
else
  fail 'sha256sum or shasum is required to verify the SoundFont.'
fi

[[ "$ACTUAL_SHA256" == "$EXPECTED_SHA256" ]] || fail "SoundFont checksum mismatch: $ACTUAL_SHA256"
printf 'Verified SoundFont: %s\n' "$ACTUAL_SHA256"

cd "$WEB_DIR"
if [[ "${SKIP_NPM_INSTALL:-0}" != "1" ]]; then
  npm install --no-audit --no-fund
fi
npm run build

cd "$ROOT"
printf '\nStarting Jampanion Web at http://localhost:%s/\n\n' "$PORT"
exec dotnet run --project "$WEB_PROJECT" --urls "http://localhost:$PORT"
