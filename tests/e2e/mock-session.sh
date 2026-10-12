#!/bin/bash
# PipeWire + mock ScreenCast portal (no compositor needed); runs "$@" inside.
export HOME=/root XDG_RUNTIME_DIR=/tmp/run XDG_SESSION_TYPE=wayland WAYLAND_DISPLAY=wayland-mock
mkdir -p $XDG_RUNTIME_DIR && chmod 700 $XDG_RUNTIME_DIR
dbus-daemon --session --address=unix:path=$XDG_RUNTIME_DIR/bus --fork --nopidfile >/dev/null
export DBUS_SESSION_BUS_ADDRESS=unix:path=$XDG_RUNTIME_DIR/bus
pipewire >/tmp/pipewire.log 2>&1 & sleep 0.5
wireplumber >/tmp/wireplumber.log 2>&1 & pipewire-pulse >/tmp/pipewire-pulse.log 2>&1 & sleep 1
# A screen-like PipeWire video source (1280x720 bouncing ball)
for n in 1 ${MOCK_SCREENS:+$(seq 2 $MOCK_SCREENS)}; do
gst-launch-1.0 -q videotestsrc pattern=$([ $n = 1 ] && echo ball || echo smpte) is-live=true ! video/x-raw,width=1280,height=720,framerate=30/1 ! \
  pipewiresink mode=provide client-name=mock-screen-$n stream-properties="props,media.class=Video/Source,node.name=mock-screen-$n" >/tmp/source-$n.log 2>&1 &
done
(gst-launch-1.0 -q audiotestsrc wave=sine is-live=true ! pulsesink >/tmp/audio.log 2>&1 &)
for i in $(seq 1 50); do
  NODE=$(pw-dump 2>/dev/null | python3 -c 'import json,sys
ids=sorted((o.get("info",{}).get("props",{}).get("node.name"), o["id"]) for o in json.load(sys.stdin) if o.get("type")=="PipeWire:Interface:Node" and str(o.get("info",{}).get("props",{}).get("node.name","")).startswith("mock-screen"))
print(",".join(str(i) for _, i in ids))')
  [ $(echo "$NODE" | tr "," "\n" | grep -c .) -ge ${MOCK_SCREENS:-1} ] && break; sleep 0.2
done
echo "mock screen node: $NODE"
MOCK_NODE=$NODE python3 /mock_portal.py >/tmp/mock.log 2>&1 &
sleep 1
"$@"
