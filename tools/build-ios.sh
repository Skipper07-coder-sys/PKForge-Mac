#!/bin/zsh
# Builds PKForge for iPhone and iPad (one screen, landscape) into dist/ios.noindex/.
#
#   tools/build-ios.sh             for the iOS Simulator on this Mac; no signing needed
#   tools/build-ios.sh device      for a real iPhone; needs an Apple ID signed into Xcode and:
#       PKFORGE_IOS_SIGN_IDENTITY  "Apple Development: Name (TEAMID)"
#       PKFORGE_IOS_PROVISION      the provisioning profile (name or UUID) for the bundle id below
#       PKFORGE_IOS_BUNDLE_ID      an app id that team owns (default org.pkforge.app)
# Needs Xcode and the .NET 10 SDK with the maui-ios workload:  dotnet workload install maui-ios
set -euo pipefail
cd "$(dirname "$0")/.."

[[ "$(uname)" == Darwin ]] || { echo "error: iOS builds need macOS and Xcode." >&2; exit 1; }
dotnet workload list 2>/dev/null | grep -q maui-ios || {
    echo "error: the maui-ios workload is missing. Run: dotnet workload install maui-ios" >&2; exit 1; }
if [[ "$(xcode-select -p 2>/dev/null)" == *CommandLineTools* && -z "${DEVELOPER_DIR:-}" ]]; then
    echo "error: xcode-select points at the Command Line Tools, not Xcode." >&2
    echo "hint: run 'sudo xcode-select -s /Applications/Xcode.app' or set DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer" >&2
    exit 1
fi

target=${1:-simulator}
case $target in
    simulator) rid=iossimulator-arm64; sign_args=() ;;
    device)
        rid=ios-arm64
        [[ -n "${PKFORGE_IOS_SIGN_IDENTITY:-}" && -n "${PKFORGE_IOS_PROVISION:-}" ]] || {
            echo "error: a device build needs PKFORGE_IOS_SIGN_IDENTITY and PKFORGE_IOS_PROVISION (see the top of this script)." >&2; exit 1; }
        sign_args=(-p:CodesignKey="$PKFORGE_IOS_SIGN_IDENTITY" -p:CodesignProvision="$PKFORGE_IOS_PROVISION")
        [[ -n "${PKFORGE_IOS_BUNDLE_ID:-}" ]] && sign_args+=(-p:ApplicationId="$PKFORGE_IOS_BUNDLE_ID")
        ;;
    *) echo "usage: tools/build-ios.sh [simulator|device]" >&2; exit 1 ;;
esac

[[ -e .git ]] && git submodule update --init --depth 1
[[ -d external/PKHeX/PKHeX.Core ]] || { echo "error: external/PKHeX/PKHeX.Core is missing (submodule not fetched)." >&2; exit 1; }
rm -rf src/PKForge.App/bin/Release/net10.0-ios/$rid src/PKForge.App/obj/Release/net10.0-ios/$rid
# PKForgeIosOnly builds the app for net10.0-ios alone, as PKForgeMacOnly does for the Mac.
dotnet build src/PKForge.App/PKForge.App.csproj -f net10.0-ios -r $rid -p:PKForgeIosOnly=true -c Release --no-incremental "${sign_args[@]}"
# ".noindex" keeps Spotlight from listing the iPhone app beside the Mac one.
out=dist/ios.noindex/$target
rm -rf $out
mkdir -p $out
# The bundle is named after the assembly (PKForge.App.app); the home screen shows PKForge either way.
mv src/PKForge.App/bin/Release/net10.0-ios/$rid/PKForge.App.app $out/PKForge.app
echo "Built $out/PKForge.app"
