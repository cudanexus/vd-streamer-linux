#!/usr/bin/env bash
# usage: build-deb.sh <stage-dir> <version> <debian-arch> <output.deb>
set -euo pipefail
STAGE="$1"; VERSION="$2"; ARCH="$3"; OUT="$4"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cp -a "$STAGE/." "$WORK/"
mkdir -p "$WORK/DEBIAN"
SIZE="$(du -sk "$WORK" | cut -f1)"
sed -e "s/@VERSION@/${VERSION}-1/" -e "s/@ARCH@/${ARCH}/" -e "s/@SIZE@/${SIZE}/" "$HERE/control.in" > "$WORK/DEBIAN/control"
for script in postinst prerm postrm; do
  install -m 0755 "$HERE/$script" "$WORK/DEBIAN/$script"
done
printf '/etc/ufw/applications.d/vdstreamer\n/etc/xdg/autostart/vdstreamer.desktop\n' > "$WORK/DEBIAN/conffiles"
dpkg-deb --root-owner-group -Zxz --build "$WORK" "$OUT" >/dev/null
echo "   $(basename "$OUT")"
