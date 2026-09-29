#!/bin/zsh
# Builds PKForge.app for macOS (universal: Apple Silicon + Intel) into dist/.
#
# Optional, for a release others can open with a double-click:
#   PKFORGE_SIGN_IDENTITY  "Developer ID Application: Name (TEAMID)" - signs with the hardened runtime
#   PKFORGE_NOTARY_PROFILE a notarytool keychain profile - also notarizes and staples
# Needs Xcode (a newer Xcode than the .NET workload pins is fine) and the .NET 10 SDK with
# the maui-maccatalyst workload:  dotnet workload install maui-maccatalyst
set -euo pipefail
cd "$(dirname "$0")/.."

[[ "$(uname)" == Darwin ]] || { echo "error: this script builds the Mac app and must run on macOS." >&2; exit 1; }
dotnet workload list 2>/dev/null | grep -q maui-maccatalyst || {
    echo "error: the maui-maccatalyst workload is missing. Run: dotnet workload install maui-maccatalyst" >&2; exit 1; }
if [[ "$(xcode-select -p 2>/dev/null)" == *CommandLineTools* && -z "${DEVELOPER_DIR:-}" ]]; then
    echo "error: xcode-select points at the Command Line Tools, not Xcode." >&2
    echo "hint: run 'sudo xcode-select -s /Applications/Xcode.app' or set DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer" >&2
    exit 1
fi

[[ -e .git ]] && git submodule update --init --depth 1
[[ -d external/PKHeX/PKHeX.Core ]] || { echo "error: external/PKHeX/PKHeX.Core is missing (submodule not fetched)." >&2; exit 1; }
# Clean every time: the universal merge step otherwise reuses a stale bundle (old Info.plist, no icon).
rm -rf src/PKForge.App/bin/Release/net10.0-maccatalyst src/PKForge.App/obj/Release/net10.0-maccatalyst
sign_args=()
if [[ -n "${PKFORGE_SIGN_IDENTITY:-}" ]]; then
    sign_args=(-p:CodesignKey="$PKFORGE_SIGN_IDENTITY" -p:UseHardenedRuntime=true)
fi
dotnet build src/PKForge.App/PKForge.App.csproj -f net10.0-maccatalyst -c Release --no-incremental "${sign_args[@]}"
mkdir -p dist
rm -rf dist/PKForge.app
cp -R src/PKForge.App/bin/Release/net10.0-maccatalyst/PKForge.app dist/
rm -f dist/PKForge-mac.zip
ditto -c -k --keepParent dist/PKForge.app dist/PKForge-mac.zip

if [[ -n "${PKFORGE_NOTARY_PROFILE:-}" ]]; then
    [[ -n "${PKFORGE_SIGN_IDENTITY:-}" ]] || { echo "error: notarizing needs PKFORGE_SIGN_IDENTITY too." >&2; exit 1; }
    xcrun notarytool submit dist/PKForge-mac.zip --keychain-profile "$PKFORGE_NOTARY_PROFILE" --wait
    xcrun stapler staple dist/PKForge.app
    # Re-zip so the download carries the stapled ticket and opens offline too.
    rm -f dist/PKForge-mac.zip
    ditto -c -k --keepParent dist/PKForge.app dist/PKForge-mac.zip
    spctl -a -vv dist/PKForge.app
fi
echo "Built dist/PKForge.app and dist/PKForge-mac.zip. Drag it to Applications."
