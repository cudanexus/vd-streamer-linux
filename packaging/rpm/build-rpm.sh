#!/usr/bin/env bash
# usage: build-rpm.sh <stage-dir> <version> <rpm-arch> <output-dir>
set -euo pipefail
STAGE="$1"; VERSION="$2"; ARCH="$3"; OUT="$4"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TOP="$(mktemp -d)"
trap 'rm -rf "$TOP"' EXIT
mkdir -p "$TOP"/{BUILD,RPMS,SOURCES,SPECS,SRPMS,BUILDROOT}
RPMVER="${VERSION%%+*}"
RPMREL="1.vd${VERSION##*+vd}"
rpmbuild -bb --quiet --target "$ARCH" \
  --define "_topdir $TOP" --define "stage $STAGE" --define "pkgversion $RPMVER" --define "pkgrelease $RPMREL" \
  --define "_build_id_links none" --define "debug_package %{nil}" --define "__strip /bin/true" \
  --define "_binaries_in_noarch_packages_terminate_build 0" --define "__brp_mangle_shebangs /bin/true" \
  --define "__check_files %{nil}" --nodeps "$HERE/vdstreamer.spec"
find "$TOP/RPMS" -name '*.rpm' -exec cp {} "$OUT/" \;
find "$TOP/RPMS" -name '*.rpm' -printf '   %f\n'
