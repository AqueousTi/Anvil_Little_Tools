#!/usr/bin/env bash
# Local test helper (dev-only): build the app once per panel alpha and measure
# the stock capsule over a white and a black backdrop, so the readability sweep
# in the report has real numbers. Restores the ShellAlpha the tree had when it
# started (a hardcoded "restore to 0xF0" silently undid later user choices).
#
#   alpha-sweep.sh 3E B4 D8 F0
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$ROOT/CrossPlatform/tools/env.sh"
SRC="$ROOT/CrossPlatform/LittleTools.Assistant/GlassSurface.cs"
LOG="$ROOT/.tools/out/alpha-sweep.txt"
: > "$LOG"
orig_alpha="$(sed -n 's/.*public const byte ShellAlpha = 0x\([0-9A-Fa-f]\{2\}\);.*/\1/p' "$SRC" | head -1)"
if [[ -z "$orig_alpha" ]]; then
  echo "cannot read the current ShellAlpha from $SRC" >&2
  exit 1
fi
echo "original ShellAlpha: 0x$orig_alpha"

for alpha in "$@"; do
  python3 - "$SRC" "$alpha" <<'PY'
import re, sys
path, alpha = sys.argv[1], sys.argv[2]
text = open(path, encoding='utf-8').read()
text = re.sub(r'public const byte ShellAlpha = 0x[0-9A-Fa-f]{2};',
              f'public const byte ShellAlpha = 0x{alpha};', text)
open(path, 'w', encoding='utf-8').write(text)
print('patched ShellAlpha to 0x' + alpha)
PY
  ( cd "$ROOT" && dotnet build CrossPlatform/LittleTools.CrossPlatform.slnx -c Release 2>&1 | tail -3 )
  bash "$ROOT/CrossPlatform/tools/measure-capsule.sh" "$alpha" 2>&1 | tee -a "$LOG"
done

python3 - "$SRC" "$orig_alpha" <<'PY'
import re, sys
path, alpha = sys.argv[1], sys.argv[2]
text = open(path, encoding='utf-8').read()
text = re.sub(r'public const byte ShellAlpha = 0x[0-9A-Fa-f]{2};',
              f'public const byte ShellAlpha = 0x{alpha};', text)
open(path, 'w', encoding='utf-8').write(text)
print('restored ShellAlpha to 0x' + alpha)
PY
