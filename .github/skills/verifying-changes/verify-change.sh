#!/usr/bin/env bash
# Scoped verification for a working-tree change in dotnet/msbuild.
#
# Maps every changed file to the test projects that cover it, builds them with
# warnings treated as errors, runs the tests, and prints a one-line verdict.
# The last line is always "RESULT: PASS" or "RESULT: FAIL".
#
# Usage:
#   .github/skills/verifying-changes/verify-change.sh [--filter "*Pattern*"] [--all-tfms] [--build-only] [--projects a.csproj,b.csproj]
#
#   --filter    xUnit v3 method filter (wildcards). Omit to run every test in the mapped projects.
#   --all-tfms  On Windows, run net472 as well as net11.0. Default is net11.0 only.
#   --build-only  Build with -warnaserror but skip the test run.
#   --projects  Override the mapping with an explicit comma-separated list of test projects.
#
# Bash only. On Windows run it from Git Bash, or follow the equivalent commands in SKILL.md.
set -u

cd "$(git rev-parse --show-toplevel)" || exit 2

FILTER=""
ALL_TFMS=0
BUILD_ONLY=0
EXPLICIT_PROJECTS=""
while [ $# -gt 0 ]; do
  case "$1" in
    --filter) FILTER="$2"; shift 2 ;;
    --all-tfms) ALL_TFMS=1; shift ;;
    --build-only) BUILD_ONLY=1; shift ;;
    --projects) EXPLICIT_PROJECTS="$2"; shift 2 ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

DOTNET="dotnet"
[ -x ./.dotnet/dotnet ] && DOTNET="./.dotnet/dotnet"

LOGDIR="artifacts/log/verify-change"
mkdir -p "$LOGDIR"
rm -f "$LOGDIR"/*.log "$LOGDIR"/*.binlog 2>/dev/null

# ---- 1. Changed files ------------------------------------------------------
CHANGED=$( { git diff --name-only HEAD; git ls-files --others --exclude-standard; } | sort -u )
if [ -z "$CHANGED" ] && [ -z "$EXPLICIT_PROJECTS" ]; then
  echo "No changed files relative to HEAD and no --projects given."
  echo "RESULT: FAIL"
  exit 1
fi

echo "== Changed files"
echo "$CHANGED" | sed 's/^/   /'

# ---- 2. Map to test projects ----------------------------------------------
# macOS ships bash 3.2 (no associative arrays): keep a newline-separated list and dedupe later.
PROJECT_LIST=""
add() { PROJECT_LIST="$PROJECT_LIST$1
"; }

T_BUILD="src/Build.UnitTests/Microsoft.Build.Engine.UnitTests.csproj"
T_BUILD_OM="src/Build.OM.UnitTests/Microsoft.Build.Engine.OM.UnitTests.csproj"
T_BUILDCHECK="src/BuildCheck.UnitTests/Microsoft.Build.BuildCheck.UnitTests.csproj"
T_FRAMEWORK="src/Framework.UnitTests/Microsoft.Build.Framework.UnitTests.csproj"
T_TASKS="src/Tasks.UnitTests/Microsoft.Build.Tasks.UnitTests.csproj"
T_UTILITIES="src/Utilities.UnitTests/Microsoft.Build.Utilities.UnitTests.csproj"
T_MSBUILD="src/MSBuild.UnitTests/Microsoft.Build.CommandLine.UnitTests.csproj"
T_STRINGTOOLS="src/StringTools.UnitTests/StringTools.UnitTests.csproj"

if [ -n "$EXPLICIT_PROJECTS" ]; then
  IFS=',' read -r -a LIST <<< "$EXPLICIT_PROJECTS"
  for p in "${LIST[@]}"; do add "$p"; done
else
  while IFS= read -r f; do
    case "$f" in
      src/Build.UnitTests/*)        add "$T_BUILD" ;;
      src/Build.OM.UnitTests/*)     add "$T_BUILD_OM" ;;
      src/BuildCheck.UnitTests/*)   add "$T_BUILDCHECK" ;;
      src/Framework.UnitTests/*)    add "$T_FRAMEWORK" ;;
      src/Tasks.UnitTests/*)        add "$T_TASKS" ;;
      src/Utilities.UnitTests/*)    add "$T_UTILITIES" ;;
      src/MSBuild.UnitTests/*)      add "$T_MSBUILD" ;;
      src/StringTools.UnitTests/*)  add "$T_STRINGTOOLS" ;;
      src/Build/BuildCheck/*|src/Framework/BuildCheck/*) add "$T_BUILDCHECK"; add "$T_BUILD" ;;
      src/Build/*)                  add "$T_BUILD" ;;
      src/Framework/*)              add "$T_FRAMEWORK"; add "$T_BUILD" ;;
      src/Tasks/*)                  add "$T_TASKS" ;;
      src/Utilities/*)              add "$T_UTILITIES" ;;
      src/MSBuild/*)                add "$T_MSBUILD" ;;
      src/StringTools/*)            add "$T_STRINGTOOLS"; add "$T_BUILD" ;;
      src/Shared/*)                 add "$T_BUILD"; add "$T_TASKS"; add "$T_UTILITIES" ;;
      src/UnitTests.Shared/*)       add "$T_BUILD" ;;
      *) ;;  # docs, eng, .github: nothing to build
    esac
  done <<< "$CHANGED"
fi

PROJECTS=$(printf '%s' "$PROJECT_LIST" | grep -v '^$' | sort -u)
if [ -z "$PROJECTS" ]; then
  echo "== No source under src/ changed; nothing to build or test."
  echo "RESULT: PASS"
  exit 0
fi

echo "== Test projects"
echo "$PROJECTS" | sed 's/^/   /'

TFM_ARGS=()
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) [ "$ALL_TFMS" -eq 1 ] || TFM_ARGS=(-f net11.0) ;;
  *) ;;  # non-Windows test projects only target net11.0
esac

FAIL=0

# ---- 3. Build with warnings as errors --------------------------------------
echo "== Build (warnings are errors)"
while IFS= read -r p; do
  name=$(basename "$p" .csproj)
  log="$LOGDIR/build-$name.log"
  echo "-- $p"
  # NuGet audit advisories (NU1901-NU1904) depend on the day's vulnerability feed, not on the
  # change under test, so they stay warnings; every compiler and MSBuild warning is an error.
  "$DOTNET" build "$p" ${TFM_ARGS[@]+"${TFM_ARGS[@]}"} -v:q -nologo \
    -warnaserror -warnnotaserror:NU1901,NU1902,NU1903,NU1904 -p:NuGetAudit=false \
    -clp:"ErrorsOnly;Summary" -flp:"v=q;LogFile=$log"
  rc=$?
  if [ $rc -ne 0 ]; then
    echo "BUILD FAILED: $p (exit $rc). First errors:"
    grep -E "error|warning" "$log" | head -20 | sed 's/^/   /'
    echo "   full log: $log"
    FAIL=1
  fi
done <<< "$PROJECTS"

if [ "$FAIL" -ne 0 ]; then
  echo "RESULT: FAIL"
  exit 1
fi
if [ "$BUILD_ONLY" -eq 1 ]; then
  echo "RESULT: PASS"
  exit 0
fi

# ---- 4. Run tests ------------------------------------------------------------
echo "== Tests${FILTER:+ (filter: $FILTER)}"
RUNNER_ARGS=()
[ -n "$FILTER" ] && RUNNER_ARGS=(--filter-method "$FILTER")

while IFS= read -r p; do
  name=$(basename "$p" .csproj)
  log="$LOGDIR/test-$name.log"
  echo "-- $p"
  # MTP mode: dotnet test forwards unknown switches to the test host, so keep the pre-"--" args minimal.
  "$DOTNET" test "$p" ${TFM_ARGS[@]+"${TFM_ARGS[@]}"} --no-build -- ${RUNNER_ARGS[@]+"${RUNNER_ARGS[@]}"} --no-progress 2>&1 | tee "$log" | grep -E "^\s*(total|failed|passed|skipped|error|duration|Test run summary)|^\s*failed [A-Za-z_.]+ \(|Exception|Shouldly" | head -80
  rc=${PIPESTATUS[0]}
  summary=$(grep -E "Test run summary" "$log" | tail -1)
  counts=$(grep -E "^\s*(total|failed|passed|skipped):" "$log" | tr -s ' \n' ' ')
  if [ $rc -ne 0 ]; then
    echo "TESTS FAILED: $p (exit $rc)"
    echo "   $summary $counts"
    echo "   full log: $log"
    FAIL=1
  else
    [ -z "$summary" ] && summary="(no summary line found; see $log)"
    echo "   $summary $counts"
    if [ -n "$FILTER" ] && grep -qE "total: 0\b|Total: 0\b|Zero tests ran" "$log"; then
      echo "   WARNING: the filter matched zero tests in this project."
    fi
  fi
done <<< "$PROJECTS"

if [ "$FAIL" -ne 0 ]; then
  echo "RESULT: FAIL"
  exit 1
fi
echo "RESULT: PASS"
