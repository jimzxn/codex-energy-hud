#!/bin/bash
# Build only: runtime verification is an explicit maintainer step.
set -euo pipefail

widget_root="$(cd "$(dirname "$0")/.." && pwd)"
widget_project="$widget_root/src/CodexHud.Mac/CodexHud.Mac.csproj"
widget_rid="${1:-}"

if [ "$(uname -s)" != Darwin ]; then
    printf '%s\n' 'Run this script on macOS with the .NET 10 SDK installed.' >&2
    exit 1
fi
if [ "$#" -gt 1 ]; then
    printf '%s\n' 'Usage: bash scripts/build-macos.sh [osx-arm64|osx-x64]' >&2
    exit 1
fi
if [ -z "$widget_rid" ]; then
    case "$(uname -m)" in
        arm64) widget_rid=osx-arm64 ;;
        x86_64) widget_rid=osx-x64 ;;
        *) printf '%s\n' 'Unsupported host architecture; specify osx-arm64 or osx-x64.' >&2; exit 1 ;;
    esac
fi
case "$widget_rid" in
    osx-arm64|osx-x64) ;;
    *) printf '%s\n' 'Supported runtime IDs: osx-arm64, osx-x64.' >&2; exit 1 ;;
esac

widget_dotnet="${DOTNET_BIN:-dotnet}"
if ! command -v "$widget_dotnet" >/dev/null 2>&1; then
    printf '%s\n' 'Install the .NET 10 SDK or set DOTNET_BIN to its executable.' >&2
    exit 1
fi
export DOTNET_CLI_HOME="$widget_root/.cache/dotnet-macos"
export NUGET_PACKAGES="$widget_root/.cache/nuget"
export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

cd "$widget_root"
widget_sdk_version="$("$widget_dotnet" --version)"
case "$widget_sdk_version" in
    10.*) ;;
    *) printf 'The selected SDK is %s; this project requires .NET 10.\n' "$widget_sdk_version" >&2; exit 1 ;;
esac
widget_version="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$widget_project" | head -n 1 | tr -d '\r')"
if [[ ! "$widget_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf '%s\n' 'The macOS project must declare a numeric three-part Version.' >&2
    exit 1
fi

widget_release_parent="$widget_root/artifacts/release/macos/$widget_rid"
mkdir -p "$widget_release_parent"
widget_stage="$(mktemp -d "$widget_release_parent/.build.XXXXXX")"
trap 'rm -rf "$widget_stage"' EXIT
widget_app="$widget_stage/CodexHud.app"
mkdir -p "$widget_app/Contents/MacOS" "$widget_app/Contents/Resources"

"$widget_dotnet" publish "$widget_project" -c Release -r "$widget_rid" \
    --self-contained true --configfile "$widget_root/NuGet.Config" \
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:UseAppHost=true \
    -o "$widget_app/Contents/MacOS"
chmod +x "$widget_app/Contents/MacOS/CodexHud.Mac"

cat > "$widget_app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleDevelopmentRegion</key><string>zh_CN</string>
  <key>CFBundleIdentifier</key><string>com.codexhud.mac</string>
  <key>CFBundleName</key><string>CodexHud</string>
  <key>CFBundleDisplayName</key><string>Codex 电量 HUD</string>
  <key>CFBundleExecutable</key><string>CodexHud.Mac</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$widget_version</string>
  <key>CFBundleVersion</key><string>$widget_version</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
printf 'APPL????' > "$widget_app/Contents/PkgInfo"
cp "$widget_root/docs/macos.md" "$widget_app/Contents/Resources/macOS.md"

# Set this explicitly only when the maintainer intends to sign the output.
# No identity is selected automatically and no notarization is requested here.
if [ -n "${CODEX_HUD_SIGN_IDENTITY:-}" ]; then
    widget_entitlements="$widget_stage/entitlements.plist"
    cat > "$widget_entitlements" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>com.apple.security.cs.allow-jit</key><true/>
</dict></plist>
PLIST
    widget_timestamp=''
    if [ "$CODEX_HUD_SIGN_IDENTITY" != - ]; then widget_timestamp=--timestamp; fi
    while IFS= read -r -d '' widget_native; do
        if /usr/bin/file -b "$widget_native" | /usr/bin/grep -q 'Mach-O'; then
            /usr/bin/codesign --force --options runtime ${widget_timestamp:+--timestamp} \
                --sign "$CODEX_HUD_SIGN_IDENTITY" "$widget_native"
        fi
    done < <(/usr/bin/find "$widget_app/Contents/MacOS" -type f -print0)
    /usr/bin/codesign --force --options runtime ${widget_timestamp:+--timestamp} \
        --entitlements "$widget_entitlements" --sign "$CODEX_HUD_SIGN_IDENTITY" "$widget_app"
fi

# Preserve the preceding build for rollback; replace only this script's fixed destination.
widget_destination="$widget_release_parent/CodexHud.app"
if [ -e "$widget_destination" ]; then
    widget_backup="$(mktemp -d "$widget_release_parent/previous.XXXXXX")"
    mv "$widget_destination" "$widget_backup/CodexHud.app"
    printf 'Previous build preserved: %s\n' "$widget_backup/CodexHud.app"
fi
mv "$widget_app" "$widget_destination"
printf 'Bundle created: %s\nNo tests or runtime checks were run by this script.\n' "$widget_destination"
