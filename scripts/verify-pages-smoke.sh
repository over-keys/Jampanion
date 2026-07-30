#!/usr/bin/env bash
set -euo pipefail

site="${1:-artifacts/jampanion-web/wwwroot}"
repository_name="${2:-Jampanion}"

required=(
  "$site/index.html"
  "$site/404.html"
  "$site/_framework/blazor.webassembly.js"
  "$site/js/jampanion-audio.js"
  "$site/js/jampanion-browser.js"
  "$site/js/spessasynth_processor.min.js"
  "$site/soundfonts/FluidR3_Jampanion.sf3"
)
for path in "${required[@]}"; do
  test -s "$path" || { echo "Missing or empty published asset: $path" >&2; exit 1; }
done

grep -Fq "<base href=\"/${repository_name}/\" />" "$site/index.html"
grep -Fq 'jampanion-web-cache-version' "$site/index.html"
if grep -Fq 'navigator.serviceWorker.register' "$site/index.html"; then
  echo 'Production service-worker registration must remain disabled.' >&2
  exit 1
fi
grep -Fq 'jampanion-audio.js' "$site/js/jampanion-audio.js"

serve_root="$(mktemp -d)"
server_pid=''
cleanup() {
  if [[ -n "$server_pid" ]]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  rm -rf "$serve_root"
}
trap cleanup EXIT

mkdir -p "$serve_root/$repository_name"
cp -a "$site/." "$serve_root/$repository_name/"
python3 -m http.server 8087 --bind 127.0.0.1 --directory "$serve_root" \
  >"$serve_root/server.log" 2>&1 &
server_pid=$!

server_ready=false
for _ in {1..100}; do
  if curl -fsS "http://127.0.0.1:8087/$repository_name/" >/dev/null; then
    server_ready=true
    break
  fi
  sleep 0.1
done
if [[ "$server_ready" != true ]]; then
  echo 'The local verification server did not start.' >&2
  cat "$serve_root/server.log" >&2
  exit 1
fi

curl -fsS "http://127.0.0.1:8087/$repository_name/_framework/blazor.webassembly.js" >/dev/null
curl -fsS "http://127.0.0.1:8087/$repository_name/js/jampanion-browser.js" >/dev/null
curl -fsS "http://127.0.0.1:8087/$repository_name/js/spessasynth_processor.min.js" >/dev/null
curl -fsSI "http://127.0.0.1:8087/$repository_name/soundfonts/FluidR3_Jampanion.sf3" >/dev/null

browser=''
for candidate in google-chrome chromium chromium-browser; do
  if command -v "$candidate" >/dev/null 2>&1; then
    browser="$candidate"
    break
  fi
done
test -n "$browser" || { echo 'No Chrome/Chromium executable was found.' >&2; exit 1; }

browser_profile="$serve_root/chrome-profile"
mkdir -p "$browser_profile"
set +e
timeout 75s "$browser" \
  --headless \
  --no-sandbox \
  --disable-gpu \
  --disable-dev-shm-usage \
  --user-data-dir="$browser_profile" \
  --window-size=1280,900 \
  --virtual-time-budget=45000 \
  --dump-dom \
  "http://127.0.0.1:8087/$repository_name/" \
  >"$serve_root/dom.html" \
  2>"$serve_root/chrome.log"
browser_status=$?
set -e

if [[ $browser_status -ne 0 ]]; then
  echo "Chrome smoke test failed with status $browser_status." >&2
  cat "$serve_root/chrome.log" >&2
  exit 1
fi

grep -Fq 'desktop-shell' "$serve_root/dom.html" || {
  echo 'The rendered Jampanion shell was not found.' >&2
  cat "$serve_root/chrome.log" >&2
  exit 1
}
grep -Fq 'chart-workspace' "$serve_root/dom.html" || {
  echo 'The rendered chord-sheet workspace was not found.' >&2
  cat "$serve_root/chrome.log" >&2
  exit 1
}
grep -Fq 'Start session' "$serve_root/dom.html" || {
  echo 'The session control was not rendered.' >&2
  cat "$serve_root/chrome.log" >&2
  exit 1
}
grep -Fq 'Chord Sheet' "$serve_root/dom.html" || {
  echo 'The chord-sheet heading was not rendered.' >&2
  cat "$serve_root/chrome.log" >&2
  exit 1
}
if grep -Fq 'class="boot-screen"' "$serve_root/dom.html"; then
  echo 'Blazor did not replace the loading screen.' >&2
  cat "$serve_root/chrome.log" >&2
  exit 1
fi

printf 'GitHub Pages browser smoke test passed at /%s/.\n' "$repository_name"
