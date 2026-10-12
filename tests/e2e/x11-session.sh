#!/bin/bash
# Xvfb + PulseAudio desktop with a moving test pattern and a test tone; runs "$@" inside.
export HOME=/root XDG_RUNTIME_DIR=/tmp/run
mkdir -p $XDG_RUNTIME_DIR /root/Videos
Xvfb :99 -screen 0 1920x1080x24 >/dev/null 2>&1 & export DISPLAY=:99
pulseaudio -D --exit-idle-time=-1 --disallow-exit 2>/dev/null; sleep 2
gst-launch-1.0 -q videotestsrc pattern=${PATTERN:-ball} is-live=true ! video/x-raw,width=1920,height=1080 ! ximagesink >/dev/null 2>&1 &
gst-launch-1.0 -q audiotestsrc wave=sine is-live=true ! pulsesink >/dev/null 2>&1 &
ffmpeg -v error -f lavfi -i testsrc2=size=1280x720:rate=30 -f lavfi -i sine=frequency=330:sample_rate=48000 -t 20 \
  -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest /root/Videos/sample.mp4
# expose uinput devices created after container start (privileged containers keep a static /dev)
( for i in $(seq 1 600); do for d in /sys/class/input/event*; do e=$(basename $d)
    [ -e /dev/input/$e ] || { mkdir -p /dev/input; mknod /dev/input/$e c $(cut -d: -f1 $d/dev) $(cut -d: -f2 $d/dev) 2>/dev/null; }
    n=$(cat $d/device/name 2>/dev/null)
    case "$n" in "Virtual Desktop"*) [ -f /e2e/$e.log ] || {
        (timeout 40 evtest /dev/input/$e > /e2e/$e.log 2>&1 &)
        # libinput (what Xorg and Wayland compositors use) needs the udev input_id properties.
        if command -v libinput >/dev/null && command -v udevadm >/dev/null; then
          mkdir -p /run/udev/data
          udevadm test-builtin input_id /sys/class/input/$e 2>/dev/null | grep '^ID_INPUT' | sed 's/^/E:/' > /run/udev/data/c$(cat $d/dev)
          (timeout 40 stdbuf -oL libinput debug-events --device /dev/input/$e > /e2e/$e.libinput.log 2>&1 &)
        fi; } ;; esac
  done; sleep 0.2; done ) &
( for attempt in $(seq 1 60); do
    if pactl list short sources 2>/dev/null | grep -q VirtualDesktopMic; then
      sleep 1
      timeout 3 parec --device=VirtualDesktopMic --format=s16le --rate=48000 --channels=1 --raw > /e2e/mic.raw.tmp 2>/dev/null
      if [ "$(stat -c%s /e2e/mic.raw.tmp 2>/dev/null || echo 0)" -gt 100000 ]; then mv /e2e/mic.raw.tmp /e2e/mic.raw; break; fi
    fi
    sleep 0.5
  done ) &
"$@"
