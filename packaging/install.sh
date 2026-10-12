#!/usr/bin/env bash
# Installs the unpacked Virtual Desktop Streamer tarball into / (run with sudo).
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ "$(id -u)" != 0 ]; then
  exec sudo "$0" "$@"
fi
rm -rf /usr/lib/vdstreamer
cp -a "$HERE/root/." /
modprobe uinput 2>/dev/null || true
udevadm control --reload-rules 2>/dev/null || true
udevadm trigger --subsystem-match=misc --sysname-match=uinput 2>/dev/null || true
systemctl --global enable vdstreamer.service >/dev/null 2>&1 || true
command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications || true
cat <<'MSG'
Virtual Desktop Streamer installed.

Runtime packages needed (names for Debian/Ubuntu - see README for Fedora/Arch):
  gstreamer1.0-plugins-{base,good,bad,ugly} gstreamer1.0-pipewire gstreamer1.0-x
  pulseaudio-utils libpulse0 libx11-6 libxrandr2 xdg-desktop-portal

Next steps (as your normal user):
  systemctl --user start vdstreamer          # or log out and back in
  vdstreamer account add meta <your Meta username>
  vdstreamer diagnose
MSG
