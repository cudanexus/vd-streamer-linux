#!/usr/bin/env bash
# Build Virtual Desktop Streamer for Linux packages.
#
#   ./build.sh --pkg ../VirtualDesktop.Streamer.Setup.pkg          # use a downloaded macOS installer
#   ./build.sh --download                                          # fetch the latest official installer
#   ./build.sh --pkg X --arch x64 --formats tar,deb,rpm
#   ./build.sh --no-vendor ...    # packages without Virtual Desktop's libraries (downloaded on first start)
#
# Outputs land in dist/. The Virtual Desktop protocol libraries come from the official macOS
# streamer package and are proprietary: packages built with them are for your own use only.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG=""
DOWNLOAD=0
ARCHES="x64 arm64"
FORMATS="tar,deb,rpm"
BUNDLE_VENDOR=1
PORT_VERSION="1.1.0"

while [ $# -gt 0 ]; do
  case "$1" in
    --pkg) PKG="$2"; shift 2 ;;
    --download) DOWNLOAD=1; shift ;;
    --arch) if [ "$2" = all ]; then ARCHES="x64 arm64"; else ARCHES="$(echo "$2" | tr ',' ' ')"; fi; shift 2 ;;
    --formats) FORMATS="$2"; shift 2 ;;
    --no-vendor) BUNDLE_VENDOR=0; shift ;;
    --version) PORT_VERSION="$2"; shift 2 ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 64 ;;
  esac
done

BUILD="$ROOT/build"
DIST="$ROOT/dist"
mkdir -p "$BUILD" "$DIST"
log() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }

# ---- .NET 8 SDK -------------------------------------------------------------------------------
find_dotnet() {
  for d in "${DOTNET:-}" "$(command -v dotnet 2>/dev/null || true)" "$ROOT/.dotnet/dotnet"; do
    if [ -n "$d" ] && [ -x "$d" ] && "$d" --list-sdks 2>/dev/null | grep -q '^8\.'; then echo "$d"; return; fi
  done
}
DOTNET_BIN="$(find_dotnet)"
if [ -z "$DOTNET_BIN" ]; then
  log "Installing a private .NET 8 SDK into .dotnet/"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$BUILD/dotnet-install.sh"
  bash "$BUILD/dotnet-install.sh" --channel 8.0 --install-dir "$ROOT/.dotnet" --no-path >/dev/null
  DOTNET_BIN="$ROOT/.dotnet/dotnet"
fi
export DOTNET_ROOT="$(dirname "$DOTNET_BIN")" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
log "Using $("$DOTNET_BIN" --version) ($DOTNET_BIN)"

# ---- vendor libraries --------------------------------------------------------------------------
"$DOTNET_BIN" build "$ROOT/src/VendorTool" -c Release -o "$BUILD/vendortool" -v quiet -nologo >/dev/null
VENDORTOOL=("$DOTNET_BIN" "$BUILD/vendortool/vdstreamer-vendor.dll")

if [ "$DOWNLOAD" = 1 ] && [ -z "$PKG" ]; then
  log "Downloading the latest official Virtual Desktop Streamer for macOS"
  URL="$(curl -fsSL https://download.vrdesktop.net/files/updatesMacOS.txt | sed -n 's/.*"url"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')"
  URL="${URL:-https://download.vrdesktop.net/files/VirtualDesktop.Streamer.Setup.pkg}"
  PKG="$BUILD/VirtualDesktop.Streamer.Setup.pkg"
  curl -fL "$URL" -o "$PKG"
fi
if [ -n "$PKG" ]; then
  log "Extracting and patching protocol libraries from $(basename "$PKG")"
  "${VENDORTOOL[@]}" --pkg "$PKG" --target "$BUILD/vendor" | grep -v '^  [+!]' || true
elif [ ! -f "$BUILD/vendor/VirtualDesktop.Net.dll" ]; then
  echo "error: pass --pkg <VirtualDesktop.Streamer.Setup.pkg> or --download" >&2
  exit 2
fi
REFPACK="$(ls -d "$DOTNET_ROOT"/packs/Microsoft.NETCore.App.Ref/8.*/ref/net8.0 | sort -V | tail -1)"
"${VENDORTOOL[@]}" --facade --vendor "$BUILD/vendor" --refpack "$REFPACK" --out "$BUILD/vendor-ref/System.Private.CoreLib.dll"
VENDOR_VERSION="$(sed -n 's/.*"version":"\([0-9.]*\)".*/\1/p' "$BUILD/vendor/vendor.json" | cut -d. -f1-3)"
VERSION="${PORT_VERSION}+vd${VENDOR_VERSION}"
log "Version $VERSION"

# ---- publish + stage ---------------------------------------------------------------------------
stage_arch() {
  local arch="$1" out="$BUILD/publish/$1" stage="$BUILD/stage/$1"
  log "Publishing linux-$arch"
  rm -rf "$out" "$stage"
  "$DOTNET_BIN" publish "$ROOT/src/VDStreamer" -c Release -r "linux-$arch" --self-contained \
    -p:Version="$PORT_VERSION" -p:DebugType=none -p:VendorDir="$BUILD/vendor" -o "$out" -v quiet -nologo
  mkdir -p "$stage/usr/lib/vdstreamer" "$stage/usr/bin" "$stage/usr/lib/systemd/user" "$stage/usr/lib/udev/rules.d" \
           "$stage/usr/lib/modules-load.d" "$stage/usr/share/applications" "$stage/usr/share/icons/hicolor/scalable/apps" \
           "$stage/usr/lib/firewalld/services" "$stage/etc/ufw/applications.d" "$stage/usr/share/doc/vdstreamer" "$stage/etc/xdg/autostart"
  cp -R "$out/." "$stage/usr/lib/vdstreamer/"
  if [ "$BUNDLE_VENDOR" = 1 ]; then
    cp -R "$BUILD/vendor" "$stage/usr/lib/vdstreamer/vendor"
  fi
  ln -sf ../lib/vdstreamer/vdstreamer "$stage/usr/bin/vdstreamer"
  cp "$ROOT/packaging/common/vdstreamer.service" "$stage/usr/lib/systemd/user/"
  cp "$ROOT/packaging/common/60-vdstreamer-uinput.rules" "$stage/usr/lib/udev/rules.d/"
  cp "$ROOT/packaging/common/vdstreamer-uinput.conf" "$stage/usr/lib/modules-load.d/"
  cp "$ROOT/packaging/common/vdstreamer.desktop" "$stage/usr/share/applications/"
  cp "$ROOT/packaging/common/vdstreamer-autostart.desktop" "$stage/etc/xdg/autostart/vdstreamer.desktop"
  cp "$ROOT/packaging/common/vdstreamer.svg" "$stage/usr/share/icons/hicolor/scalable/apps/"
  cp "$ROOT/packaging/common/vdstreamer-firewalld.xml" "$stage/usr/lib/firewalld/services/vdstreamer.xml"
  cp "$ROOT/packaging/common/ufw-vdstreamer" "$stage/etc/ufw/applications.d/vdstreamer"
  cp "$ROOT/README.md" "$stage/usr/share/doc/vdstreamer/"
  chmod 755 "$stage/usr/lib/vdstreamer/vdstreamer"
}

docker_ok() { command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; }

for arch in $ARCHES; do
  stage_arch "$arch"
  stage="$BUILD/stage/$arch"
  case "$arch" in x64) debarch=amd64; rpmarch=x86_64; dplat=linux/amd64 ;; arm64) debarch=arm64; rpmarch=aarch64; dplat=linux/arm64 ;; esac

  if [[ ",$FORMATS," == *,tar,* ]]; then
    tarball="$DIST/vdstreamer-$VERSION-linux-$arch.tar.gz"
    log "Creating $(basename "$tarball")"
    tmp="$BUILD/tar/vdstreamer-$VERSION-linux-$arch"
    rm -rf "$BUILD/tar"; mkdir -p "$tmp"
    cp -R "$stage/." "$tmp/root"
    cp "$ROOT/packaging/install.sh" "$ROOT/packaging/uninstall.sh" "$tmp/"
    chmod +x "$tmp/install.sh" "$tmp/uninstall.sh"
    tar -C "$BUILD/tar" -czf "$tarball" "vdstreamer-$VERSION-linux-$arch"
  fi

  if [[ ",$FORMATS," == *,deb,* ]]; then
    deb="$DIST/vdstreamer_${VERSION}-1_${debarch}.deb"
    log "Creating $(basename "$deb")"
    if command -v dpkg-deb >/dev/null 2>&1; then
      "$ROOT/packaging/debian/build-deb.sh" "$stage" "$VERSION" "$debarch" "$deb"
    elif docker_ok; then
      docker run --rm -v "$ROOT:/src" -v "$BUILD:/build" -v "$DIST:/dist" debian:trixie-slim \
        bash /src/packaging/debian/build-deb.sh "/build/stage/$arch" "$VERSION" "$debarch" "/dist/$(basename "$deb")"
    else
      echo "   skipped: needs dpkg-deb or docker" >&2
    fi
  fi

  if [[ ",$FORMATS," == *,rpm,* ]]; then
    log "Creating RPM for $rpmarch"
    if command -v rpmbuild >/dev/null 2>&1; then
      "$ROOT/packaging/rpm/build-rpm.sh" "$stage" "$VERSION" "$rpmarch" "$DIST"
    elif docker_ok; then
      docker run --rm -v "$ROOT:/src" -v "$BUILD:/build" -v "$DIST:/dist" fedora:41 \
        bash -c "dnf -q -y install rpm-build >/dev/null 2>&1 && bash /src/packaging/rpm/build-rpm.sh /build/stage/$arch $VERSION $rpmarch /dist"
    else
      echo "   skipped: needs rpmbuild or docker" >&2
    fi
  fi
done

log "Done:"
ls -1 "$DIST"
