#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
ARCH="${1:-$(uname -m)}"
case "$ARCH" in
  arm64) RID=osx-arm64 ;;
  x86_64) RID=osx-x64 ;;
  *) echo "Usage: Mac/build.sh [arm64|x86_64]" >&2; exit 2 ;;
esac
DOTNET="${DOTNET:-dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
BUILD="$ROOT/Mac/.build/$ARCH"
OUTPUT="$ROOT/artifacts/macos-$ARCH"
# Stage outside file-provider folders: Finder metadata can race codesign in Documents.
STAGING="$(mktemp -d "${TMPDIR:-/tmp}/rgbroom-build.XXXXXX")"
APP="$STAGING/RGBroom.app"
mkdir -p "$BUILD" "$APP/Contents/MacOS" "$APP/Contents/Resources/Engine" "$OUTPUT"
xcrun swiftc -swift-version 5 -O -emit-library -target "$ARCH-apple-macosx14.0" \
  -module-cache-path "$BUILD/module-cache" Mac/native/Bridge.swift -o Mac/native/libRGBroomNative.dylib
"$DOTNET" build Mac/RGBroom.Mac.csproj -c Release
if [[ "$ARCH" == "$(uname -m)" ]]; then
  export MAGICHOME_TEST_REPORT="$BUILD/test-report.txt"
  : > "$MAGICHOME_TEST_REPORT"
  "$DOTNET" Mac/bin/Release/net10.0/RGBroom.Engine.dll --self-test
  cat "$MAGICHOME_TEST_REPORT"
fi
"$DOTNET" publish Mac/RGBroom.Mac.csproj -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -o "$APP/Contents/Resources/Engine"
xcrun swiftc -swift-version 5 -O -target "$ARCH-apple-macosx14.0" \
  -module-cache-path "$BUILD/module-cache" Mac/Models.swift Mac/RGBroomApp.swift -o "$APP/Contents/MacOS/RGBroom"
cp Mac/Info.plist "$APP/Contents/Info.plist"
# Compile against the installed SDK so system controls adopt that macOS design.
SDK_VERSION="$(xcrun --show-sdk-version)"
/usr/libexec/PlistBuddy -c "Add :DTSDKName string macosx$SDK_VERSION" "$APP/Contents/Info.plist"
ICONSET="$BUILD/RGBroom.iconset"
mkdir -p "$ICONSET"
for SIZE in 16 32 128 256 512; do
  sips -z "$SIZE" "$SIZE" docs/images/icon.png --out "$ICONSET/icon_${SIZE}x${SIZE}.png" >/dev/null
  DOUBLE=$((SIZE * 2))
  sips -z "$DOUBLE" "$DOUBLE" docs/images/icon.png --out "$ICONSET/icon_${SIZE}x${SIZE}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/RGBroom.icns"
cp LICENSE COPYING Mac/THIRD-PARTY-NOTICES.txt "$APP/Contents/Resources/"
cp -R Licenses "$APP/Contents/Resources/"
# Finder/iCloud can attach metadata to a newly created .app in Documents.
# Remove only the metadata forbidden by codesign, not quarantine attributes.
xattr -dr com.apple.FinderInfo "$APP" 2>/dev/null || true
xattr -dr com.apple.ResourceFork "$APP" 2>/dev/null || true
# Local ad-hoc signature. Distribution requires Developer ID and notarization.
find "$APP/Contents/Resources/Engine" -type f \( -name '*.dylib' -o -name 'RGBroom.Engine' -o -name 'createdump' \) -print0 |
  while IFS= read -r -d '' FILE; do codesign --force --sign - --entitlements Mac/entitlements.plist "$FILE"; done
codesign --force --sign - --entitlements Mac/entitlements.plist "$APP"
codesign --verify --deep --strict "$APP"
if [[ -e "$OUTPUT/RGBroom.app" ]]; then
  mv "$OUTPUT/RGBroom.app" "$BUILD/RGBroom-previous-$(date +%s).app"
fi
ditto "$APP" "$OUTPUT/RGBroom.app"
xattr -dr com.apple.FinderInfo "$OUTPUT/RGBroom.app" 2>/dev/null || true
codesign --verify --deep --strict "$OUTPUT/RGBroom.app"
echo "Built: $OUTPUT/RGBroom.app"
