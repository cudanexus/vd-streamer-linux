#!/usr/bin/env bash
# End-to-end tests in containers: a Linux PC running vdstreamer and a simulated headset.
#   tests/e2e/run.sh <published linux build dir (with vendor/)> <published HeadsetSim dir>
set -uo pipefail
OUT="$(cd "$1" && pwd)"; SIM="$(cd "$2" && pwd)"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULTS="${RESULTS:-$HERE/results}"; mkdir -p "$RESULTS"
docker build -q -t vdstreamer-e2e-x11 -f "$HERE/Dockerfile.x11" "$HERE" >/dev/null
docker build -q -t vdstreamer-e2e-wayland -f "$HERE/Dockerfile.wayland" "$HERE" >/dev/null
docker network inspect vdtest >/dev/null 2>&1 || docker network create --internal vdtest >/dev/null
PASS=0; FAIL=0

scenario() { # name image session pc-setup sim-args checks...
  local name="$1" image="$2" session="$3" setup="$4" simargs="$5"; shift 5
  local dir="$RESULTS/$name-$(date +%s)"; mkdir -p "$dir"
  docker rm -f vdA vdB >/dev/null 2>&1
  docker run -d --name vdA --privileged --network vdtest -e MOCK_SCREENS="${MOCK_SCREENS:-1}" \
    -v "$OUT":/opt/vdstreamer:ro -v "$HERE":/t:ro -v "$HERE/../portal-mock/mock_portal.py":/mock_portal.py:ro -v "$dir":/e2e \
    "$image" /t/$session bash -c "export VDSTREAMER_LOG_LEVEL=Debug; /opt/vdstreamer/vdstreamer account add meta vdtest-user >/dev/null; $setup /opt/vdstreamer/vdstreamer run > /e2e/streamer.out 2>&1" >/dev/null
  sleep 12
  docker run --rm --name vdB --network vdtest -v "$SIM":/opt/sim:ro -v "$OUT/vendor":/opt/vdstreamer/vendor:ro -v "$dir":/e2e \
    vdstreamer-e2e-x11 bash -c "cd /e2e && timeout 150 /opt/sim/HeadsetSim --account vdtest-user --out /e2e/received.h264 $simargs" > "$dir/sim.log" 2>&1
  sleep 1
  docker exec vdA bash -c 'cp /root/.local/share/vdstreamer/logs/StreamerLog.txt /e2e/ 2>/dev/null' >/dev/null 2>&1
  local ok=1 why=""
  grep -q '\[sim\] PASS' "$dir/sim.log" || { ok=0; why="sim: $(grep '\[sim\]' "$dir/sim.log" | tail -1)"; }
  for check in "$@"; do
    if ! (cd "$dir" && eval "$check") >/dev/null 2>&1; then ok=0; why="$why [check failed: $check]"; fi
  done
  if [ $ok = 1 ]; then PASS=$((PASS+1)); echo "PASS  $name  ($(grep -o 'received.*' "$dir/sim.log" | head -1))";
  else FAIL=$((FAIL+1)); echo "FAIL  $name  $why  -> $dir"; fi
  docker rm -f vdA >/dev/null 2>&1
}

decodes='docker run --rm -v "$PWD":/r vdstreamer-e2e-x11 ffmpeg -v error -i /r/received.h264 -f null -'
scenario x11-annexb       vdstreamer-e2e-x11     x11-session.sh  ""  "--seconds 12" "$decodes" "grep -q 'KiB audio' sim.log && ! grep -q ' 0 KiB audio' sim.log"
scenario x11-avcc         vdstreamer-e2e-x11     x11-session.sh  ""  "--seconds 8 --annexb false" "$decodes"
scenario x11-input-mic    vdstreamer-e2e-x11     x11-session.sh  ""  "--seconds 12 --input --mic" \
  "grep -q KEY_A event*.log" "grep -q REL_X event*.log" "grep -q BTN_LEFT event*.log" "test \$(stat -c%s mic.raw 2>/dev/null || stat -f%z mic.raw) -gt 100000"
scenario x11-video-file   vdstreamer-e2e-x11     x11-session.sh  ""  "--seconds 12 --play-video sample.mp4"
scenario x11-hevc         vdstreamer-e2e-x11     x11-session.sh  "/opt/vdstreamer/vdstreamer set preferredCodec hevc >/dev/null;"  "--seconds 8" "grep -q 'HEVC' sim.log"
MOCK_SCREENS=1 scenario wayland-portal  vdstreamer-e2e-wayland mock-session.sh "" "--seconds 10" "$decodes" "grep -q 'Portal screencast started' StreamerLog.txt"
MOCK_SCREENS=2 scenario wayland-2-screens vdstreamer-e2e-wayland mock-session.sh "/opt/vdstreamer/vdstreamer set monitorCount 2 >/dev/null;" "--seconds 8" "grep -q 'streams=2' sim.log" "! grep -q ' 0 slices' sim.log"
echo "== $PASS passed, $FAIL failed"
[ $FAIL = 0 ]
