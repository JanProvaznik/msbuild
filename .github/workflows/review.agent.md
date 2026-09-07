---
name: "Expert Code Review (command)"
description: "Reviews a pull request when an authorized maintainer comments /review; the coordinator publishes through safe outputs."

on:
  slash_command:
    name: review
    events: [pull_request_comment]
  roles: [admin, maintainer, write]

permissions:
  contents: read
  pull-requests: read

# A checkout mapping alone does not suppress gh-aw's issue-comment PR-head checkout.
checkout: false
steps:
  - name: Checkout trusted workflow revision
    uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
    with:
      repository: ${{ github.repository }}
      ref: ${{ github.sha }}
      persist-credentials: false

timeout-minutes: 60

# ###############################################################
# Disable the per-workflow daily AI Credits guardrail. Cost is
# limited by the authorized slash-command trigger and configured
# run limits; this exception does not authorize unbounded delegation.
# See dotnet/msbuild#14312.
# ###############################################################
max-daily-ai-credits: -1

# ###############################################################
# Select a PAT from the pool and override COPILOT_GITHUB_TOKEN.
# Run agentic jobs in an isolated `copilot-pat-pool` environment.
#
# When org-level billing is available, this will be removed.
# See `shared/pat_pool.README.md` for more information.
# ###############################################################
imports:
  - uses: shared/pat_pool.md
    with:
      environment: copilot-pat-pool
  - shared/review-shared.md

environment: copilot-pat-pool

engine:
  id: copilot
  env:
     COPILOT_GITHUB_TOKEN: "${{ case( needs.pat_pool.outputs.pat_number == '0', secrets.COPILOT_PAT_0, needs.pat_pool.outputs.pat_number == '1', secrets.COPILOT_PAT_1, needs.pat_pool.outputs.pat_number == '2', secrets.COPILOT_PAT_2, needs.pat_pool.outputs.pat_number == '3', secrets.COPILOT_PAT_3, needs.pat_pool.outputs.pat_number == '4', secrets.COPILOT_PAT_4, needs.pat_pool.outputs.pat_number == '5', secrets.COPILOT_PAT_5, needs.pat_pool.outputs.pat_number == '6', secrets.COPILOT_PAT_6, needs.pat_pool.outputs.pat_number == '7', secrets.COPILOT_PAT_7, needs.pat_pool.outputs.pat_number == '8', secrets.COPILOT_PAT_8, needs.pat_pool.outputs.pat_number == '9', secrets.COPILOT_PAT_9, 'NO COPILOT PAT AVAILABLE') }}"
---

<!-- Body provided by shared/review-shared.md -->
