#!/bin/bash
# Package an existing macOS bundle; do not build, sign, or run it.
set -euo pipefail

widget_root="$(cd "$(dirname "$0")/.." && pwd)"
widget_rid="${1:-}"
if [ "$(uname -s)" != Darwin ]; then
    printf '%s\n' 'Run this script on macOS; ditto preserves bundle permissions and symbolic links.' >&2
    exit 1
fi
if [ "$#" -gt 1 ]; then
    printf '%s\n' 'Usage: bash scripts/package-macos.sh [osx-arm64|osx-x64]' >&2
    exit 1
fi
if [ -z "$widget_rid" ]; then
    case "$(uname -m)" in
        arm64) widget_rid=osx-arm64 ;;
        x86_64) widget_rid=osx-x64 ;;
        *) printf '%s\n' 'Unsupported host architecture.' >&2; exit 1 ;;
    esac
fi
case "$widget_rid" in
    osx-arm64|osx-x64) ;;
    *) printf '%s\n' 'Supported runtime IDs: osx-arm64, osx-x64.' >&2; exit 1 ;;
esac

widget_app="$widget_root/artifacts/release/macos/$widget_rid/CodexHud.app"
if [ ! -x "$widget_app/Contents/MacOS/CodexHud.Mac" ] || [ ! -f "$widget_app/Contents/Info.plist" ]; then
    printf 'Build the bundle first: bash scripts/build-macos.sh %s\n' "$widget_rid" >&2
    exit 1
fi
widget_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$widget_app/Contents/Info.plist")"
if [[ ! "$widget_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf '%s\n' 'The bundle must contain a numeric three-part version.' >&2
    exit 1
fi
widget_artifacts="$widget_root/artifacts"
widget_stage="$(mktemp -d "$widget_artifacts/.mac-package.XXXXXX")"
trap 'rm -rf "$widget_stage"' EXIT
widget_payload="$widget_stage/CodexHud"
mkdir -p "$widget_payload/docs"
/usr/bin/ditto "$widget_app" "$widget_payload/CodexHud.app"
# Explicit allowlist: no settings, Codex records, local evidence, or source archive.
cp "$widget_root/README.md" "$widget_payload/README.md"
cp "$widget_root/docs/macos.md" "$widget_payload/docs/macos.md"
cp "$widget_root/docs/architecture.md" "$widget_payload/docs/architecture.md"
mkdir -p "$widget_payload/docs/assets"
cp "$widget_root/docs/assets/readme-hero.svg" "$widget_payload/docs/assets/readme-hero.svg"
widget_archive="$widget_artifacts/CodexHud-$widget_version-$widget_rid.zip"
/usr/bin/ditto -c -k --sequesterRsrc --keepParent "$widget_payload" "$widget_stage/release.zip"
mv -f "$widget_stage/release.zip" "$widget_archive"
(
    cd "$widget_artifacts"
    /usr/bin/shasum -a 256 "$(basename "$widget_archive")" > "$(basename "$widget_archive").sha256"
)
printf 'Package created: %s\nChecksum: %s.sha256\nPackaging is not runtime validation.\n' "$widget_archive" "$widget_archive"
