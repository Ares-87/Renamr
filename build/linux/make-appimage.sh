#!/usr/bin/env bash
# Builds a one-file Renamr AppImage from a self-contained publish folder.
# Usage: build/linux/make-appimage.sh <publish-dir> <linux-x64|linux-arm64> <output.AppImage>
# Needs appimagetool (x86_64) on PATH or in $APPIMAGETOOL; for arm64 it also downloads the aarch64 runtime.
set -euo pipefail

publish=$1
rid=$2
output=$3
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
tool=${APPIMAGETOOL:-appimagetool}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

case "$rid" in
  linux-x64)   arch=x86_64 ;;
  linux-arm64) arch=aarch64 ;;
  *) echo "Unsupported RID: $rid" >&2; exit 1 ;;
esac

appdir="$work/Renamr.AppDir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/icons/hicolor/512x512/apps"
cp -a "$publish/." "$appdir/usr/bin/"
install -m 755 "$here/AppRun" "$appdir/AppRun"
cp "$here/renamr.desktop" "$appdir/renamr.desktop"
cp "$repo/assets/brand/renamr-icon-512.png" "$appdir/renamr.png"
cp "$repo/assets/brand/renamr-icon-512.png" "$appdir/usr/share/icons/hicolor/512x512/apps/renamr.png"
ln -s renamr.png "$appdir/.DirIcon"

runtime=()
if [[ "$arch" != "x86_64" ]]; then
  curl -fsSL -o "$work/runtime" "https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-$arch"
  runtime=(--runtime-file "$work/runtime")
fi

ARCH=$arch APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream "${runtime[@]}" "$appdir" "$output"
chmod +x "$output"
