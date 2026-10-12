#!/usr/bin/env bash
# End-to-end tests against an installed .deb (e.g. on Ubuntu 20.04) with a simulated headset.
#   tests/e2e/run-deb.sh <image with vdstreamer installed> <published HeadsetSim dir> <vendor dir>
# Covers video, audio, input incl. scrolling through libinput, the unchanged-frame filter and
# latency on a slow link (with and without network pacing).
set -uo pipefail
IMAGE="$1"; SIM="$(cd "$2" && pwd)"; VENDOR="$(cd "$3" && pwd)"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULTS="${RESULTS:-$HERE/results}"; mkdir -p "$RESULTS"
docker build -q -t vdstreamer-e2e-x11 -f "$HERE/Dockerfile.x11" "$HERE" >/dev/null
docker network inspect vdtest >/dev/null 2>&1 || docker network create --internal vdtest >/dev/null
PASS=0; FAIL=0

scenario() { # name pc-env pc-setup sim-args checks...
  local name="$1" env="$2" setup="$3" simargs="$4"; shift 4
  local dir="$RESULTS/deb-$name-$(date +%s)"; mkdir -p "$dir"
  docker rm -f vdA vdB >/dev/null 2>&1
  docker run -d --name vdA --privileged --network vdtest $env -v "$HERE":/t:ro -v "$dir":/e2e \
    "$IMAGE" /t/x11-session.sh bash -c "export VDSTREAMER_LOG_LEVEL=Debug; vdstreamer account add meta vdtest-user >/dev/null; $setup vdstreamer run > /e2e/streamer.out 2>&1" >/dev/null
  sleep 14
  docker run --rm --name vdB --network vdtest -v "$SIM":/opt/sim:ro -v "$VENDOR":/opt/vdstreamer/vendor:ro -v "$dir":/e2e \
    vdstreamer-e2e-x11 bash -c "cd /e2e && timeout 150 /opt/sim/HeadsetSim --account vdtest-user --out /e2e/received.h264 $simargs" > "$dir/sim.log" 2>&1
  sleep 2
  docker exec vdA bash -c 'cp /root/.local/share/vdstreamer/logs/StreamerLog.txt /e2e/ 2>/dev/null; top -b -n1 > /e2e/top.txt' >/dev/null 2>&1
  local ok=1 why=""
  grep -q '\[sim\] PASS' "$dir/sim.log" || { ok=0; why="sim: $(grep '\[sim\]' "$dir/sim.log" | tail -1)"; }
  for check in "$@"; do
    if ! (cd "$dir" && eval "$check") >/dev/null 2>&1; then ok=0; why="$why [check failed: $check]"; fi
  done
  local delay; delay="$(grep -o 'queueing delay.*' "$dir/sim.log" | head -1)"
  if [ $ok = 1 ]; then PASS=$((PASS+1)); echo "PASS  $name  ($(grep -o 'received [0-9]* frames' "$dir/sim.log" | head -1); $delay)";
  else FAIL=$((FAIL+1)); echo "FAIL  $name  $why  -> $dir"; fi
  docker rm -f vdA >/dev/null 2>&1
}

decodes='docker run --rm -v "$PWD":/r vdstreamer-e2e-x11 ffmpeg -v error -i /r/received.h264 -f null -'
device_log() { echo "grep -l 'Input device name: \"$1\"' \$(ls event*.log | grep -v libinput)"; }
max_delay_below() { echo "test \$(grep -o 'queueing delay: max [0-9]*' sim.log | grep -o '[0-9]*\$') -lt $1"; }

scenario video-audio  "" "" "--seconds 12" "$decodes" "grep -q 'KiB audio' sim.log && ! grep -q ' 0 KiB audio' sim.log"
scenario input-scroll "" "" "--seconds 14 --input --scroll --mic --input-delay 3" \
  "grep -q KEY_A event*.log" "grep -q REL_X event*.log" "grep -q BTN_LEFT event*.log" \
  "$(device_log 'Virtual Desktop Pointer') | xargs grep -q '(REL_WHEEL), value'" \
  "$(device_log 'Virtual Desktop Pointer') | xargs grep -q '(REL_WHEEL_HI_RES), value 8$'" \
  "grep -q 'POINTER_AXIS' event*.libinput.log" "grep -q 'POINTER_MOTION_ABSOLUTE' event*.libinput.log" \
  "test \$(stat -c%s mic.raw 2>/dev/null || stat -f%z mic.raw) -gt 100000"
scenario static-screen "-e PATTERN=checkers-8" "" "--seconds 12" "$decodes" "grep -Eq 'Stream statistics: .* [1-9][0-9]+ unchanged frames skipped' StreamerLog.txt"
scenario slow-link "-e PATTERN=snow" "" "--seconds 25 --read-mbps 8" "$(max_delay_below 1500)" "grep -q 'lowering video bitrate' StreamerLog.txt"
scenario slow-link-no-pacing "-e PATTERN=snow -e VDSTREAMER_NO_PACING=1" "/usr/bin/vdstreamer set adaptiveBitrate false >/dev/null;" "--seconds 25 --read-mbps 8"
echo "== $PASS passed, $FAIL failed"
[ $FAIL = 0 ]
