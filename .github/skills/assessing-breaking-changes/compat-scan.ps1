<#
.SYNOPSIS
  Mechanical backward-compatibility scan of the working-tree diff in dotnet/msbuild.
  Prints one SIGNAL per compatibility lead, a "WAVE:" verdict, and "RESULT: PASS", "RESULT: REVIEW" or "RESULT: FAIL" last.
  It cannot judge intent; it makes sure the question is asked and answered in writing.
.EXAMPLE
  .\.github\skills\assessing-breaking-changes\compat-scan.ps1
  .\.github\skills\assessing-breaking-changes\compat-scan.ps1 -Acknowledge "warning is inside the new opt-in switch"
#>
[CmdletBinding()]
param(
    [string]$Acknowledge = '',   # Turns FAIL into REVIEW and records the reason. Only for a signal inside new, opt-in behavior.
    [string]$Base = 'HEAD'       # Compare against this ref.
)
$ErrorActionPreference = 'Continue'
Set-Location (git rev-parse --show-toplevel)

# Collect added and removed lines, each tagged with its file. Untracked files count as entirely added.
$added = New-Object System.Collections.Generic.List[object]
$removed = New-Object System.Collections.Generic.List[object]
$file = ''
foreach ($l in @(git diff $Base -U0 -- . ':(exclude)artifacts')) {
    if ($l -match '^\+\+\+ b/(.*)$') { $file = $Matches[1] }
    elseif ($l -match '^(\+\+\+|---) ') { }
    elseif ($l -match '^\+(.*)$') { $added.Add([pscustomobject]@{ File = $file; Line = $Matches[1] }) }
    elseif ($l -match '^-(.*)$') { $removed.Add([pscustomobject]@{ File = $file; Line = $Matches[1] }) }
}
foreach ($f in @(git ls-files --others --exclude-standard)) {
    if (Test-Path $f -PathType Leaf) { foreach ($l in Get-Content $f) { $added.Add([pscustomobject]@{ File = $f; Line = $l }) } }
}
if ($added.Count -eq 0 -and $removed.Count -eq 0) { Write-Host "No changes relative to $Base."; Write-Host 'WAVE: NOT NEEDED'; Write-Host 'RESULT: PASS'; exit 0 }
$files = @(@($added) + @($removed) | ForEach-Object File | Sort-Object -Unique)
$both = @($added) + @($removed)
Write-Host "== Compatibility scan against $Base"; Write-Host "== Files: $($files.Count)"

$script:required = $false; $script:review = $false
function Signal([string]$Level, [string]$Name, $Hits) {
    if (-not $Hits) { return }
    if ($Level -eq 'REQUIRED') { $script:required = $true } else { $script:review = $true }
    Write-Host "SIGNAL [$Level] $Name"
    @($Hits) | Select-Object -First 6 | ForEach-Object { Write-Host "     $_" }
}
# Lines in $Set whose file matches $FileRx and whose text matches $LineRx, formatted "file: text".
function Lines($Set, [string]$FileRx, [string]$LineRx) {
    @($Set | Where-Object { $_.File -match $FileRx -and $_.Line -match $LineRx } | ForEach-Object { "$($_.File): $($_.Line.Trim())" })
}

Signal REVIEW   'new-or-changed-diagnostic-text'                                  (Lines $added   'Strings\.resx$' '<value>MSB\d{4}:\s*\S')
Signal REQUIRED 'new-warning-emitted (breaks WarnAsError builds unless opt-in or wave-gated)' (Lines $added '^src/(Build|Tasks|Utilities|Shared|MSBuild|Framework)/.*\.cs$' '\bLogWarning\w*\(')
Signal REQUIRED 'cli-switch-removed-or-renamed (never remove; deprecate first)'    (Lines $removed '^src/MSBuild/CommandLine/CommandLineSwitches\.cs$' '.')
Signal REQUIRED 'property-default-or-condition-changed-in-shipped-targets'        (Lines $both    '^src/(Tasks|MSBuild)/.*\.(targets|props)$' 'Condition="''\$\(|<[A-Za-z]+ *Condition=')
Signal REQUIRED 'public-api-removed-or-changed (deprecate with [Obsolete], never remove)' (Lines $removed '^src/(Build|Framework|Tasks|Utilities)/.*\.cs$' '^\s*public\s+')
Signal REVIEW   'public-api-added (needs justification; default to internal)'     (Lines $added   '^src/(Build|Framework|Tasks|Utilities)/.*\.cs$' '^\s*public\s+')
Signal REVIEW   'ipc-packet-or-translation-changed (old nodes must still talk to new ones)' (Lines $both '\.cs$' 'ITranslatable|\.Translate\(|NodePacketType|INodePacket')
Signal REVIEW   'message-importance-changed (changes console and binlog output)'  (Lines $both    '\.cs$' 'MessageImportance\.')
Signal REVIEW   'binary-log-format-touched (see maintaining-binary-log-compatibility)' ($files -match 'BinaryLogger|BuildEventArgsWriter|BuildEventArgsReader|BinaryLogRecordKind|ProjectImportsCollector')
Signal REVIEW   'evaluation-semantics-touched (property/item/condition evaluation order is a contract)' ($files -match '^src/Build/Evaluation/(Evaluator|Expander|LazyItemEvaluator|IntrinsicFunctions|ConditionEvaluator)')
Signal REQUIRED 'pinned-infra-file-changed (only on explicit request; dependency bumps go through Darc)' ($files -match '^(global\.json|NuGet\.config|eng/Versions\.props|eng/Version\.Details\.xml)$')

# A ChangeWave gate in added product lines satisfies REQUIRED signals; it must come with a ChangeWaves.md entry.
$gate = Lines $added '^src/.*\.(cs|targets|props)$' 'AreFeaturesEnabled'
if ($gate) {
    Write-Host 'GATE: ChangeWaves.AreFeaturesEnabled present in added product lines'
    $gate | Select-Object -First 5 | ForEach-Object { Write-Host "     $_" }
    if ($files -notcontains 'documentation/wiki/ChangeWaves.md') { Signal REVIEW 'changewave-used-but-documentation/wiki/ChangeWaves.md-not-updated' 'add an entry under the current rotation' }
}

Write-Host ''
if ($script:required -and -not $gate) {
    Write-Host 'WAVE: REQUIRED (a REQUIRED signal fired and no ChangeWaves.AreFeaturesEnabled gate was added)'
    if ($Acknowledge) { Write-Host "ACKNOWLEDGED: $Acknowledge"; Write-Host 'RESULT: REVIEW'; exit 0 }
    Write-Host 'Next: load the changewaves skill and gate the behavior, or re-run with -Acknowledge "<why this is opt-in or not user-visible>".'
    Write-Host 'RESULT: FAIL'; exit 1
} elseif ($script:required -or $script:review) {
    Write-Host 'WAVE: REVIEW (answer each SIGNAL above in the PR''s Compatibility section)'
    Write-Host 'RESULT: REVIEW'; exit 0
} else {
    Write-Host "WAVE: NOT NEEDED (no user-visible signals detected; still state 'no behavior change' in the PR)"
    Write-Host 'RESULT: PASS'; exit 0
}
