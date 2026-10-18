#!/usr/bin/env bash
# Mechanical backward-compatibility scan of the working-tree diff in dotnet/msbuild.
#
# Looks at added and removed lines for the signals that most often mean a change
# is user-visible, then says whether a ChangeWave gate is required, worth a
# review, or not needed. It cannot judge intent; it makes sure the question is
# asked and answered in writing.
#
# Usage:
#   .github/skills/assessing-breaking-changes/compat-scan.sh [--acknowledge "<reason>"] [--base <ref>]
#
#   --acknowledge  Turns a FAIL into REVIEW and prints the reason. Use only when
#                  a flagged signal is inside new, opt-in behavior, and say so.
#   --base         Compare against this ref instead of HEAD.
#
# Last line is "RESULT: PASS", "RESULT: REVIEW", or "RESULT: FAIL".
set -u
cd "$(git rev-parse --show-toplevel)" || exit 2

ACK=""
BASE="HEAD"
while [ $# -gt 0 ]; do
  case "$1" in
    --acknowledge) ACK="$2"; shift 2 ;;
    --base) BASE="$2"; shift 2 ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# Unified diff of tracked changes plus synthetic diffs for untracked files.
git diff "$BASE" -U0 -- . ':(exclude)artifacts' > "$TMP/diff"
git ls-files --others --exclude-standard | while IFS= read -r f; do
  [ -f "$f" ] || continue
  git diff --no-index -U0 -- /dev/null "$f" >> "$TMP/diff" 2>/dev/null
done

if [ ! -s "$TMP/diff" ]; then
  echo "No changes relative to $BASE."
  echo "WAVE: NOT NEEDED"
  echo "RESULT: PASS"
  exit 0
fi

# Split into per-file added/removed line streams: "<file>\t<line>".
awk '
  /^\+\+\+ / { f=$2; sub(/^b\//,"",f); next }
  /^--- /    { next }
  /^\+/      { print f "\t" substr($0,2) > "'"$TMP"'/added" }
  /^-/       { print f "\t" substr($0,2) > "'"$TMP"'/removed" }
' "$TMP/diff"
touch "$TMP/added" "$TMP/removed"

CHANGED_FILES=$(awk -F'\t' '{print $1}' "$TMP/added" "$TMP/removed" | sort -u)

REQUIRED=0
REVIEW=0
signal() { # level name detail
  if [ "$1" = "REQUIRED" ]; then REQUIRED=1; else REVIEW=1; fi
  echo "SIGNAL [$1] $2"
  echo "$3" | head -6 | sed 's/^/     /'
}

echo "== Compatibility scan against $BASE"
echo "== Files: $(echo "$CHANGED_FILES" | grep -c . )"

# 1. New diagnostics in resx.
hit=$(grep -E $'^[^\t]*Strings\\.resx\t.*<value>MSB[0-9]{4}:' "$TMP/added" | grep -vE 'MSB[0-9]{4}: ?$' || true)
[ -n "$hit" ] && signal REVIEW "new-or-changed-diagnostic-text" "$hit"

# 2. New warning calls in product code.
hit=$(grep -E $'^src/(Build|Tasks|Utilities|Shared|MSBuild|Framework)/[^\t]*\\.cs\t.*\\bLogWarning[A-Za-z]*\\(' "$TMP/added" || true)
[ -n "$hit" ] && signal REQUIRED "new-warning-emitted (breaks WarnAsError builds unless opt-in or wave-gated)" "$hit"

# 3. Command-line switch table changes.
hit=$(grep -E $'^src/MSBuild/CommandLine/CommandLineSwitches\\.cs\t' "$TMP/removed" || true)
[ -n "$hit" ] && signal REQUIRED "cli-switch-removed-or-renamed (never remove; deprecate first)" "$hit"

# 4. Property defaults in shipped targets/props.
hit=$(grep -E $'^src/(Tasks|MSBuild)/[^\t]*\\.(targets|props)\t.*(Condition="\'\\$\\(|<[A-Za-z]+ *Condition=)' "$TMP/added" "$TMP/removed" || true)
[ -n "$hit" ] && signal REQUIRED "property-default-or-condition-changed-in-shipped-targets" "$hit"

# 5. Public API surface.
hit=$(grep -E $'^src/(Build|Framework|Tasks|Utilities)/[^\t]*\\.cs\t\\s*public\\s+(static\\s+|abstract\\s+|virtual\\s+|override\\s+|sealed\\s+|readonly\\s+)*(class|interface|struct|enum|delegate|[A-Za-z_<>,\\[\\] ?]+\\s+[A-Za-z_]+\\s*(\\(|\\{|=|;))' "$TMP/removed" || true)
[ -n "$hit" ] && signal REQUIRED "public-api-removed-or-changed (deprecate with [Obsolete], never remove)" "$hit"
hit=$(grep -E $'^src/(Build|Framework|Tasks|Utilities)/[^\t]*\\.cs\t\\s*public\\s+' "$TMP/added" || true)
[ -n "$hit" ] && signal REVIEW "public-api-added (needs justification; default to internal)" "$hit"

# 6. IPC wire format.
hit=$(grep -E $'\\.cs\t.*(ITranslatable|\\.Translate\\(|NodePacketType|INodePacket)' "$TMP/added" "$TMP/removed" || true)
[ -n "$hit" ] && signal REVIEW "ipc-packet-or-translation-changed (old nodes must still talk to new ones)" "$hit"

# 7. Message importance.
hit=$(grep -E $'\\.cs\t.*MessageImportance\\.' "$TMP/added" "$TMP/removed" || true)
[ -n "$hit" ] && signal REVIEW "message-importance-changed (changes console and binlog output)" "$hit"

# 8. Binary log.
hit=$(echo "$CHANGED_FILES" | grep -E 'BinaryLogger|BuildEventArgsWriter|BuildEventArgsReader|BinaryLogRecordKind|ProjectImportsCollector' || true)
[ -n "$hit" ] && signal REVIEW "binary-log-format-touched (see maintaining-binary-log-compatibility)" "$hit"

# 9. Evaluation and expansion hot paths.
hit=$(echo "$CHANGED_FILES" | grep -E 'src/Build/Evaluation/(Evaluator|Expander|LazyItemEvaluator|IntrinsicFunctions|ConditionEvaluator)' || true)
[ -n "$hit" ] && signal REVIEW "evaluation-semantics-touched (property/item/condition evaluation order is a contract)" "$hit"

# 10. Pinned infrastructure files.
hit=$(echo "$CHANGED_FILES" | grep -E '^(global\.json|NuGet\.config|eng/Versions\.props|eng/Version\.Details\.xml)$' || true)
[ -n "$hit" ] && signal REQUIRED "pinned-infra-file-changed (only on explicit request; dependency bumps go through Darc)" "$hit"

# Gate detection.
GATED=0
if grep -qE $'^src/[^\t]*\\.(cs|targets|props)\t.*ChangeWaves\\.AreFeaturesEnabled|^src/[^\t]*\\.(targets|props)\t.*AreFeaturesEnabled\\(' "$TMP/added"; then
  GATED=1
  echo "GATE: ChangeWaves.AreFeaturesEnabled present in added product lines"
  grep -E $'^src/[^\t]*\\.(cs|targets|props)\t.*AreFeaturesEnabled' "$TMP/added" | sed 's/^/     /' | head -5
  if ! echo "$CHANGED_FILES" | grep -q '^documentation/wiki/ChangeWaves\.md$'; then
    signal REVIEW "changewave-used-but-documentation/wiki/ChangeWaves.md-not-updated" "add an entry under the current rotation"
  fi
fi

echo
if [ "$REQUIRED" -eq 1 ] && [ "$GATED" -eq 0 ]; then
  echo "WAVE: REQUIRED (a REQUIRED signal fired and no ChangeWaves.AreFeaturesEnabled gate was added)"
  if [ -n "$ACK" ]; then
    echo "ACKNOWLEDGED: $ACK"
    echo "RESULT: REVIEW"
    exit 0
  fi
  echo "Next: load the changewaves skill and gate the behavior, or re-run with --acknowledge \"<why this is opt-in or not user-visible>\"."
  echo "RESULT: FAIL"
  exit 1
elif [ "$REQUIRED" -eq 1 ] || [ "$REVIEW" -eq 1 ]; then
  echo "WAVE: REVIEW (answer each SIGNAL above in the PR's Compatibility section)"
  echo "RESULT: REVIEW"
  exit 0
else
  echo "WAVE: NOT NEEDED (no user-visible signals detected; still state 'no behavior change' in the PR)"
  echo "RESULT: PASS"
  exit 0
fi
