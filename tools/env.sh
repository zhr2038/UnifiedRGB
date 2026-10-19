# Source from Git Bash: `. tools/env.sh` then use `dn <args>` instead of `dotnet`.
# The sandboxed shell lacks ProgramFiles(x86) & co., which makes NuGet restore fail with "path1 null".
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
dn() {
  env 'ProgramFiles(x86)=C:\Program Files (x86)' 'ProgramFiles=C:\Program Files' 'ProgramData=C:\ProgramData' \
      'CommonProgramFiles=C:\Program Files\Common Files' 'CommonProgramFiles(x86)=C:\Program Files (x86)\Common Files' \
      "${URGB_DOTNET:-dotnet}" "$@"
}
# Terse solution build: unique errors/warnings only.
bld() { dn build src/UnifiedRgb.sln -warnaserror "$@" 2>&1 | grep -E " error | warning |已用时间|Time Elapsed" | sed 's/ \[D:.*//;s/^D:.*unified-rgb-controller.//' | sort -u | head -80; }
# Terse test run: failures + per-assembly summary lines.
tst() { dn test src/UnifiedRgb.sln --no-build "$@" 2>&1 | grep -E "失败|Failed|通过!|Passed!|错误|error|\[FAIL\]|Expected|but found|Assert|   at UnifiedRgb" | head -80; }
