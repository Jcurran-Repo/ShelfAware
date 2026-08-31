#!/usr/bin/env bash
# Turn a failed diff-scoped mutation run into a CLEAR, instant answer on the PR: an inline GitHub
# annotation on every surviving mutant's exact line (red in the "Files changed" diff and on the
# check) plus a plain-English job summary saying what to do. Without this a contributor only sees
# Stryker's "Process completed with exit code 2" and has to dig through the log to learn why the
# merge is blocked.
#
# Invoked by mutation-pr.yml ONLY when the Stryker step failed. Best-effort and never fails the job
# itself (the Stryker step already owns the red) — so every path ends `exit 0`.
set -uo pipefail

summary="${GITHUB_STEP_SUMMARY:-/dev/stdout}"
docs="${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY:-}/blob/master/docs/mutation-testing.md"

report="$(find tests/ShelfAware.Tests/StrykerOutput -path '*/reports/mutation-report.json' 2>/dev/null | sort | tail -1)"
if [ -z "${report}" ] || [ ! -f "${report}" ]; then
  {
    echo "### ❌ Mutation gate failed"
    echo ""
    echo "The mutation step failed but produced no report — see the step log above."
  } >> "${summary}"
  exit 0
fi

# A survivor is a mutant a test should have killed but didn't (Survived), or that no test even
# covered (NoCoverage). Timeouts count as killed and never lower the score, so they are not listed.
# Emit TSV: absolute-path <tab> line <tab> mutator <tab> status.
mapfile -t rows < <(jq -r '
  .files | to_entries[] | .key as $f | .value.mutants[]
  | select(.status == "Survived" or .status == "NoCoverage")
  | [$f, (.location.start.line | tostring), .mutatorName, .status] | @tsv' "${report}" 2>/dev/null)

if [ "${#rows[@]}" -eq 0 ]; then
  {
    echo "### ❌ Mutation gate failed"
    echo ""
    echo "The mutation score is below the 100% break threshold, but no surviving mutant could be read"
    echo "from the report — see the step log or the **mutation-report-pr** artifact on this run."
  } >> "${summary}"
  exit 0
fi

{
  echo "### ❌ Mutation gate failed — ${#rows[@]} mutant(s) survived"
  echo ""
  echo "\`ShelfAware.Core\` is held at a 100% mutation score. Each row below is a change to Core that"
  echo "**no test caught**, so the merge is blocked. To go green, for **each** survivor either:"
  echo ""
  echo "- **add or strengthen a test** so it fails when that mutation is applied (a real coverage gap), or"
  echo "- if it is a genuine **equivalent mutant** (no observable behaviour change), annotate it in code"
  echo "  with \`// Stryker disable once <kind> : <reason>\` — see [docs/mutation-testing.md](${docs})."
  echo ""
  echo "Reproduce locally from \`tests/ShelfAware.Tests\`: \`dotnet stryker --since:master\`. The full HTML"
  echo "report is the **mutation-report-pr** artifact on this run."
  echo ""
  echo "| File | Line | Surviving mutation | Status |"
  echo "|------|-----:|--------------------|--------|"
} >> "${summary}"

ws="${GITHUB_WORKSPACE:-}"
[ -n "${ws}" ] && ws="${ws%/}/"
for row in "${rows[@]}"; do
  IFS=$'\t' read -r path line mutator status <<< "${row}"
  rel="${path#"${ws}"}"   # repo-relative path, so the annotation lands on the PR diff
  echo "::error file=${rel},line=${line}::Mutation survived (${mutator}): no test kills this. Add a test, or annotate it as an equivalent mutant with a reason (docs/mutation-testing.md)."
  echo "| \`${rel}\` | ${line} | ${mutator} | ${status} |" >> "${summary}"
done

exit 0
