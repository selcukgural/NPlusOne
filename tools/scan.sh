#!/usr/bin/env bash
# Runs the analyzer (and its summary generator) over another solution without changing it: copies
# the solution to a temporary directory (so its bin/obj stay untouched), injects the freshly built
# analyzer through CustomAfterMicrosoftCommonTargets, raises NP0006/NP0007 to warnings, and prints
# the N+1 diagnostics.
#
#   tools/scan.sh /path/to/repo [solution-or-project relative to the repo]
#
# Only scan code you trust: building runs the solution's own MSBuild targets.
set -euo pipefail
repo="$(cd "$1" && pwd)"
target="${2:-}"
here="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

dotnet build "$here/src/NPlusOne.Analyzers" -c Release -nologo -v q >/dev/null
dll="$here/src/NPlusOne.Analyzers/bin/Release/netstandard2.0/NPlusOne.Analyzers.dll"

cat > "$work/inject.globalconfig" <<CFG
is_global = true
global_level = 100
dotnet_diagnostic.NP0006.severity = warning
dotnet_diagnostic.NP0007.severity = warning
CFG
cat > "$work/inject.targets" <<TARGETS
<Project>
  <ItemGroup>
    <Analyzer Include="$dll" />
    <GlobalAnalyzerConfigFiles Include="$work/inject.globalconfig" />
  </ItemGroup>
  <PropertyGroup>
    <WarningsNotAsErrors>\$(WarningsNotAsErrors);NP0001;NP0002;NP0003;NP0004;NP0005;NP0006;NP0007</WarningsNotAsErrors>
  </PropertyGroup>
</Project>
TARGETS

# Names with a backslash (a "bin\Debug" folder left by a Windows-style path) break MSBuild's globs on macOS and Linux.
rsync -a --exclude bin --exclude obj --exclude .git --exclude .idea --exclude node_modules --exclude '*\\*' "$repo/" "$work/src/"
cd "$work/src"
if [ -z "$target" ]; then
  target="$(find . -maxdepth 2 \( -name '*.slnx' -o -name '*.sln' \) | head -1)"
fi

# A global.json asking for an SDK this machine does not have would stop the build before it starts: pin the copy
# to the SDK this repository builds with (it says so on stderr, since the result can differ from the project's own SDK).
sdk="$(cd "$here" && dotnet --version)"
for json in $(find . -name global.json -not -path '*/node_modules/*'); do
  wanted="$(sed -nE 's/.*"version"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' "$json" | head -1)"
  if [ -n "$wanted" ] && [ ! -d "$(dirname "$(command -v dotnet)")/sdk/$wanted" ] && [ ! -d "$HOME/.dotnet/sdk/$wanted" ]; then
    echo "scan: $json asks for SDK $wanted, not installed; building with $sdk" >&2
    printf '{ "sdk": { "version": "%s", "rollForward": "latestPatch" } }\n' "$sdk" > "$json"
  fi
done
# Without a global.json of its own the copy would get the newest SDK installed, a preview one too: one above the copy
# pins this repository's SDK, and a global.json in the project still wins, being nearer.
printf '{ "sdk": { "version": "%s", "rollForward": "latestPatch" } }\n' "$sdk" > "$work/global.json"

log="$work/build.log"
dotnet build "$target" --no-incremental -nologo \
  -p:CustomAfterMicrosoftCommonTargets="$work/inject.targets" -p:TreatWarningsAsErrors=false \
  "-p:NoWarn=NU1605%3BNU1608" > "$log" 2>&1 || true
grep -E "warning NP|AD0001" "$log" | sed -E -e 's# \[[^]]*\]$##' -e "s#^.*$(basename "$work")/src/##" | sort -u || true

# A build that did not get through says so: no diagnostics from it means nothing was analysed.
if ! grep -q "Build succeeded" "$log"; then
  echo "scan: the build failed; only the projects compiled before the failure were analysed. Last errors:" >&2
  { grep -E "error|SDK" "$log" || tail -15 "$log"; } | sed -e "s#$work/src/##g" -e "s#/private$work/src/##g" | sort -u | tail -15 >&2
  exit 2
fi
