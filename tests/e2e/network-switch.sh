#!/usr/bin/env bash
# Network change test: the PC moves from one LAN to another while the streamer runs (like
# switching Wi-Fi networks); a headset on the new LAN must still find it and connect.
#   tests/e2e/network-switch.sh <image with vdstreamer installed> <published HeadsetSim dir> <vendor dir> [idle|session]
# idle:    no headset connected while the network changes.
# session: a headset is streaming on the old network when it disappears (its connection dies).
set -uo pipefail
IMAGE="$1"; SIM="$(cd "$2" && pwd)"; VENDOR="$(cd "$3" && pwd)"; MODE="${4:-idle}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULTS="${RESULTS:-$HERE/results}"; dir="$RESULTS/network-switch-$MODE-$(date +%s)"; mkdir -p "$dir"
for n in vdlanA vdlanB; do docker network inspect $n >/dev/null 2>&1 || docker network create --internal $n >/dev/null; done
docker rm -f vdA vdB vdOld >/dev/null 2>&1
docker run -d --name vdA --privileged --network vdlanA -v "$HERE":/t:ro -v "$dir":/e2e "$IMAGE" /t/x11-session.sh \
  bash -c "vdstreamer account add meta vdtest-user >/dev/null; vdstreamer run > /e2e/streamer.out 2>&1" >/dev/null
sleep 14
if [ "$MODE" = session ]; then
  docker run -d --name vdOld --network vdlanA -v "$SIM":/opt/sim:ro -v "$VENDOR":/opt/vdstreamer/vendor:ro -v "$dir":/e2e \
    vdstreamer-e2e-x11 bash -c "cd /e2e && timeout 120 /opt/sim/HeadsetSim --account vdtest-user --out /e2e/old.h264 --seconds 100 > /e2e/sim-old.log 2>&1" >/dev/null
  sleep 10
  grep -q VideoFormat "$dir/sim-old.log" && echo "headset streaming on the old network" || echo "warning: first headset did not connect"
fi
echo "before: $(docker exec vdA hostname -I)"
docker network connect vdlanB vdA && docker network disconnect vdlanA vdA
echo "after:  $(docker exec vdA hostname -I)"
sleep 15
docker run --rm --name vdB --network vdlanB -v "$SIM":/opt/sim:ro -v "$VENDOR":/opt/vdstreamer/vendor:ro -v "$dir":/e2e \
  vdstreamer-e2e-x11 bash -c "cd /e2e && timeout 90 /opt/sim/HeadsetSim --account vdtest-user --out /e2e/received.h264 --seconds 8" > "$dir/sim.log" 2>&1
docker exec vdA bash -c 'cp /root/.local/share/vdstreamer/logs/StreamerLog.txt /e2e/' >/dev/null 2>&1
docker rm -f vdA vdOld >/dev/null 2>&1
grep -h "Network changed\|Listening for headsets\|went away\|Headset disconnected\|Headset connected" "$dir/StreamerLog.txt" | cut -d'|' -f1,4- | tail -6
if grep -q '\[sim\] PASS' "$dir/sim.log"; then echo "PASS  network-switch-$MODE ($(grep -o 'received [0-9]* frames' "$dir/sim.log"))"; else echo "FAIL  network-switch-$MODE: $(grep '\[sim\]' "$dir/sim.log" | tail -1) -> $dir"; exit 1; fi
