#!/usr/bin/env bash
set -euo pipefail

failed=0

current_paths="$({
  while IFS= read -r -d '' path; do
    if [[ -e "$path" ]]; then
      printf '%s\n' "$path"
    fi
  done < <(git ls-files -z --cached --others --exclude-standard)
} | LC_ALL=C sort -u)"

if grep -E '(^|/)(bin|obj)/|\.(xlsx|xls|csv|tsv|bak|bacpac)$' <<<"$current_paths" >/dev/null; then
  echo "Tracked generated output or data extract detected."
  grep -E '(^|/)(bin|obj)/|\.(xlsx|xls|csv|tsv|bak|bacpac)$' <<<"$current_paths" || true
  failed=1
fi

if git grep -I -n -E '"ConnectionStrings"[[:space:]]*:' -- '**/appsettings*.json' >/dev/null 2>&1; then
  echo "Tracked appsettings contains a ConnectionStrings section. Use an external provider."
  git grep -I -n -E '"ConnectionStrings"[[:space:]]*:' -- '**/appsettings*.json' || true
  failed=1
fi

if git grep -I -n -E '(Server|Data Source)[[:space:]]*=[^;]+;.*(Database|Initial Catalog)[[:space:]]*=' \
    -- . \
    ':(exclude)docs/**' \
    ':(exclude).github/scripts/check-tracked-safety.sh' >/dev/null 2>&1; then
  echo "Operational-looking SQL connection string detected in tracked source."
  failed=1
fi

if git grep -I -n -E '(-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|github_pat_[A-Za-z0-9_]{20,}|ghp_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9_-]{20,})' \
    -- . \
    ':(exclude)docs/**' \
    ':(exclude).github/scripts/check-tracked-safety.sh' >/dev/null 2>&1; then
  echo "High-confidence secret pattern detected in tracked source."
  failed=1
fi

if [[ "$failed" -ne 0 ]]; then
  exit 1
fi

echo "Tracked-file and high-confidence secret checks passed."
