#!/usr/bin/env bash
set -euo pipefail
if [ "$(id -u)" != 0 ]; then
  exec sudo "$0" "$@"
fi
systemctl --global disable vdstreamer.service >/dev/null 2>&1 || true
rm -rf /usr/lib/vdstreamer /usr/bin/vdstreamer /usr/lib/systemd/user/vdstreamer.service \
  /usr/lib/udev/rules.d/60-vdstreamer-uinput.rules /usr/lib/modules-load.d/vdstreamer-uinput.conf \
  /usr/share/applications/vdstreamer.desktop /usr/share/icons/hicolor/scalable/apps/vdstreamer.svg \
  /usr/lib/firewalld/services/vdstreamer.xml /etc/ufw/applications.d/vdstreamer /etc/xdg/autostart/vdstreamer.desktop /usr/share/doc/vdstreamer
udevadm control --reload-rules 2>/dev/null || true
echo "Removed. Per-user data is in ~/.config/vdstreamer and ~/.local/share/vdstreamer."
