#!/bin/zsh
# Builds the test variant of the Mac app into dist/test.noindex/PKForge.app: Apple Silicon only,
# Release (so timings mean something), with PKF_AUTOMATION = the loopback remote control that
# tools/uitest drives (Platforms/MacCatalyst/Automation.cs) and the Perf timing marks.
# Never install this build: tools/build-mac.sh makes the app for daily use.
# Incremental (its own RID folder, so it never touches the universal build): ~20 s after the first.
set -euo pipefail
cd "$(dirname "$0")/.."
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"

dotnet build src/PKForge.App/PKForge.App.csproj -f net10.0-maccatalyst -c Release \
    -p:PKForgeMacOnly=true -p:PKForgeAutomation=true -p:RuntimeIdentifier=maccatalyst-arm64 "$@"
# `.noindex` keeps Spotlight from listing test builds as a second PKForge.
mkdir -p dist/test.noindex
rm -rf dist/test.noindex/PKForge.app
cp -cR src/PKForge.App/bin/Release/net10.0-maccatalyst/maccatalyst-arm64/PKForge.app dist/test.noindex/
echo "Built dist/test.noindex/PKForge.app (automation build, do not install)."
