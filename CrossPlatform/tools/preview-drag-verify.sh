#!/usr/bin/env bash
# Real-pointer proof that the magnified screenshot viewer ("译图预览") can be
# dragged — the fix for the user report "窗口不能拖动".
#
# Root cause it guards: Avalonia targets pointer events through the renderer's
# draw list (CompositionDrawListVisual.HitTest -> DrawList.HitTest). A Grid with
# Background=null records no drawing, so the header strip was never a hit target:
# a press on the title *text* hit the TextBlock and bubbled up to the header, but
# a press on the empty part of the strip fell through to the shell Border and the
# header's PointerPressed never ran. The header is now Transparent (hit-testable
# across its whole width), the outer Border is a fallback drag surface, and the
# picture drags the window when it is not magnified past its viewport.
#
# The viewer closes when it loses the focus, so every spot is measured on a fresh
# instance instead of trying to move the same window four times. X11 hands the
# interactive move to the window manager, so the position is read only after a
# settle delay.
#
#   preview-drag-verify.sh [outdir]
#
# Needs: an X display (default DISPLAY=:1), the workspace Release build and
# .tools/x11tool + .tools/cursorprobe (see CrossPlatform/tools/README.md).
set -uo pipefail

WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"

OUT="${1:-$WORKSPACE_ROOT/.tools/out/preview-drag}"
# Short isolated TMPDIR: dotnet names its single-instance pipes under TMPDIR and
# AF_UNIX paths are capped near 108 chars (see CrossPlatform/tools/README.md).
export TMPDIR=/tmp/lt-preview-drag
export XDG_CONFIG_HOME="$WORKSPACE_ROOT/.tools/xdg/config"
export XDG_DATA_HOME="$WORKSPACE_ROOT/.tools/xdg/data"
export XDG_CACHE_HOME="$WORKSPACE_ROOT/.tools/xdg/cache"
export DISPLAY="${DISPLAY:-:1}"
export AVALONIA_TELEMETRY_OPTOUT=1
mkdir -p "$OUT" "$TMPDIR" "$XDG_CONFIG_HOME" "$XDG_DATA_HOME" "$XDG_CACHE_HOME"

APP="$WORKSPACE_ROOT/CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll"
X11="$WORKSPACE_ROOT/.tools/x11tool"
PROBE="$WORKSPACE_ROOT/.tools/cursorprobe"
EVIDENCE="$OUT/evidence.txt"
: > "$EVIDENCE"
note() { echo "$*" | tee -a "$EVIDENCE"; }

pass=0; fail=0
ok()  { note "PASS  $1"; pass=$((pass + 1)); }
bad() { note "FAIL  $1"; fail=$((fail + 1)); }

win_id() { xwininfo -root -tree 2>/dev/null | grep -F "\"$1\"" | grep -F '("dotnet"' | head -1 | awk '{print $1}'; }
# "x y w h", space separated, so callers can read all four at once.
pos() { xwininfo -id "$1" 2>/dev/null | awk -F: '/Absolute upper-left X/{x=$2} /Absolute upper-left Y/{y=$2} /^  Width/{w=$2} /^  Height/{h=$2} END{gsub(/ /,"",x);gsub(/ /,"",y);gsub(/ /,"",w);gsub(/ /,"",h);print x" "y" "w" "h}'; }
# "x,y" only, for the before/after comparison.
geom() { pos "$1" | awk '{print $1","$2}'; }
state() { xwininfo -id "$1" 2>/dev/null | grep 'Map State' | awk -F: '{print $2}' | xargs; }
cursor_at() { "$X11" move "$1" "$2" >/dev/null; sleep 0.5; "$PROBE" | head -1; }

stop() { [[ -n "${pid:-}" ]] && { kill "$pid" 2>/dev/null; sleep 0.4; kill -9 "$pid" 2>/dev/null; wait "$pid" 2>/dev/null; }; pid=""; }
trap stop EXIT

# start_instance <tag>: fresh production window with locally planted translations.
# Sets the globals pid and MAIN_ID (no command substitution: the pid must survive
# so the EXIT trap can reap the instance).
start_instance() {
  local tag="$1"
  local log="$OUT/$1.log"
  MAIN_ID=""
  : > "$log"
  setsid "$DOTNET_ROOT/dotnet" "$APP" --preview-smoke "$OUT/planted-$tag" --preview-hold >>"$log" 2>&1 </dev/null &
  pid=$!
  for _ in $(seq 1 60); do sleep 0.4; MAIN_ID="$(win_id 'Little Tools AI')"; [[ -n "$MAIN_ID" && "$(state "$MAIN_ID")" == "IsViewable" ]] && break; done
  [[ -n "$MAIN_ID" ]] || return 1
  for _ in $(seq 1 40); do [[ -f "$OUT/planted-$tag.metrics.txt" ]] && break; sleep 0.4; done
  return 0
}

# open_preview <tag>: click the planted result image centre; sets PREVIEW_ID.
open_preview() {
  local tag="$1" metrics win_x win_y img_cx img_cy scale click_x click_y
  PREVIEW_ID=""
  metrics="$(cat "$OUT/planted-$tag.metrics.txt" 2>/dev/null)"
  read -r win_x win_y <<<"$(sed -n 's/^windowPositionPx=\([0-9-]*\),\([0-9-]*\)$/\1 \2/p' <<<"$metrics")"
  read -r img_cx img_cy <<<"$(sed -n 's/^imageCenterInWindowDip=\([0-9.]*\),\([0-9.]*\)$/\1 \2/p' <<<"$metrics")"
  scale="$(sed -n 's/^renderScaling=//p' <<<"$metrics")"
  if [[ -z "${win_x:-}" || -z "${img_cx:-}" || -z "${scale:-}" ]]; then
    echo "  (could not read the planted metrics file $OUT/planted-$tag.metrics.txt)" >&2
    return 1
  fi
  click_x=$(python3 -c "print(int(round($win_x + $img_cx * $scale)))")
  click_y=$(python3 -c "print(int(round($win_y + $img_cy * $scale)))")
  sleep 0.5
  "$X11" click "$click_x" "$click_y" >/dev/null
  for _ in $(seq 1 25); do sleep 0.3; PREVIEW_ID="$(win_id 'Little Tools · 译图预览')"; [[ -n "$PREVIEW_ID" ]] && break; done
  [[ -n "$PREVIEW_ID" ]]
}

# drag_spot <tag> <kind>: fresh instance, drag that surface, report the move.
#   text  - the header title text (worked before the fix)
#   gap   - the empty header stretch between text and close button (dead before)
#   grip  - the little pill at the left of the header
#   image - the picture (fits at the initial zoom, so it moves the window)
drag_spot() {
  local tag="$1" kind="$2" pv px py pw ph sx sy before after cur
  start_instance "$tag" || { bad "$kind: the translation window never appeared"; stop; return; }
  open_preview "$tag" || { bad "$kind: the preview window never opened"; stop; return; }
  pv="$PREVIEW_ID"
  read -r px py pw ph <<<"$(pos "$pv")"
  case "$kind" in
    text)  sx=$((px + 30));                sy=$((py + 27)) ;;
    gap)   sx=$((px + pw - 15 - 27 - 45)); sy=$((py + 27)) ;;
    grip)  sx=$((px + 28));                sy=$((py + 27)) ;;
    image) sx=$((px + pw / 2));            sy=$((py + ph / 2)) ;;
  esac
  before="$(geom "$pv")"
  cur="$(cursor_at "$sx" "$sy")"
  "$X11" drag "$sx" "$sy" "$((sx + 120))" "$((sy + 70))" 0 >/dev/null
  # X11: the WM finishes the interactive move asynchronously.
  sleep 1.2
  if [[ -z "$(win_id 'Little Tools · 译图预览')" ]]; then
    bad "$kind: the viewer disappeared during the drag"
  else
    after="$(geom "$pv")"
    [[ "$before" != "$after" ]] && ok "$kind: window moved $before -> $after, ${pw}x${ph} (cursor $cur)" \
                               || bad "$kind: window did not move from $before (cursor $cur)"
  fi
  stop
}

# Cursor contrast: the toolbar buttons keep the plain arrow.
cursor_contrast() {
  local px py pw ph
  start_instance buttons || { bad "cursor: no window"; stop; return; }
  open_preview buttons || { bad "cursor: no preview"; stop; return; }
  read -r px py pw ph <<<"$(pos "$PREVIEW_ID")"
  note "cursor over 适应窗口 button (arrow expected): $(cursor_at $((px + 45)) $((py + 59)))"
  note "cursor over header strip (move expected):     $(cursor_at $((px + pw - 15 - 27 - 45)) $((py + 27)))"
  note "cursor over picture (move expected):           $(cursor_at $((px + pw / 2)) $((py + ph / 2)))"
  stop
}

note "== preview drag verification ($(date +%H:%M:%S)) =="
for kind in text gap grip image; do drag_spot "$kind" "$kind"; done
cursor_contrast
note "pass=$pass fail=$fail"
note "evidence: $EVIDENCE"
[[ "$fail" == 0 ]]
