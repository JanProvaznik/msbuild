#!/usr/bin/env python3
"""PreToolUse hook for Bash: enforce the repo rules that agents most often break.

Blocks (exit 2, reason on stderr):
  - MSBuild/dotnet build or test output redirected or piped into a file (AGENTS.md: never do this).
  - Commands that write under artifacts/ or .dotnet/ by hand, or edit the pinned files.
  - Destructive git operations on the working tree without an explicit user request.
Everything else passes through unchanged.
"""
import json
import re
import sys

try:
    payload = json.load(sys.stdin)
except Exception:
    sys.exit(0)

cmd = (payload.get("tool_input") or {}).get("command") or ""
if not cmd:
    sys.exit(0)

def block(reason: str) -> None:
    sys.stderr.write(reason + "\n")
    sys.exit(2)

build_cmd = re.compile(r"(^|[;&|]\s*)(\./)?(build\.(sh|cmd)|dotnet\s+(build|test|msbuild)|msbuild(\.exe)?)\b", re.I)
if build_cmd.search(cmd):
    if re.search(r"\s(>|>>|\|\s*tee)\s*\S", cmd) and not re.search(r"2>&1\s*\|\s*(grep|tail|head)", cmd):
        block("Blocked: build/test output must not be redirected to a file. "
              "Use -v:q and -flp:\"v=q;LogFile=...\" or .github/skills/verifying-changes/verify-change.sh instead.")

if re.search(r"(^|\s)(rm|mv|cp|touch|sed\s+-i[^|]*|tee)\s[^|;&]*\b(artifacts|\.dotnet)/", cmd):
    block("Blocked: artifacts/ and .dotnet/ are generated; do not modify them by hand.")

if re.search(r"(^|\s)(sed\s+-i|tee|>|>>)[^|;&]*\b(global\.json|NuGet\.config)\b", cmd):
    block("Blocked: global.json and NuGet.config are pinned; change them only on explicit user request, with Edit, not shell.")

if re.search(r"\bgit\s+(checkout|restore)\s+(--\s+)?\.(\s|$)|\bgit\s+reset\s+--hard|\bgit\s+clean\s+-[a-z]*f|\bgit\s+stash\s+drop|\bgit\s+push\s+[^\n]*--force", cmd):
    block("Blocked: destructive git operation. Ask the user first; the working tree may hold uncommitted work.")

sys.exit(0)
