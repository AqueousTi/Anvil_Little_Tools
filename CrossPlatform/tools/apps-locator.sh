#!/usr/bin/env bash
# apps-locator.sh — 把「Show Applications」(应用网格) 里的图标对应到磁盘上的真实安装位置
#
# 原理: GNOME 的 Show Applications 网格本身不存路径, 它只是把下面这些目录里的
#       *.desktop 文件渲染成图标。查到 .desktop -> 读 Exec= -> 解析出真正的可执行
#       文件 -> 再用 dpkg/snap/flatpak 反查是哪个包装的。
#
# 用法:
#   ./apps-locator.sh              # 列出网格里所有应用 + 安装位置
#   ./apps-locator.sh steam        # 只查匹配 "steam" 的应用 (名称/命令/包名 模糊匹配)
#   ./apps-locator.sh -v steam     # 额外显示 .desktop 文件路径和 Exec 原文
set -u -o pipefail

DESKTOP_DIRS=(
  "$HOME/.local/share/applications"
  /usr/local/share/applications
  /usr/share/applications
  /var/lib/snapd/desktop/applications
  /var/lib/flatpak/exports/share/applications
  "$HOME/.local/share/flatpak/exports/share/applications"
)

VERBOSE=0
[ "${1:-}" = "-v" ] && { VERBOSE=1; shift; }
QUERY="${1:-}"

# 从 Exec= 里解析出可执行文件 (处理引号 / env 前缀 / %字段码)
parse_exec() {
  local line="$1" tok
  line=$(printf '%s\n' "$line" | sed -E 's/%[fFuUdDnNickvm]//g' | tr -d '"')
  # shellcheck disable=SC2086
  set -- $line
  while [ $# -gt 0 ]; do
    case "$1" in
      env|nice|nohup|setsid|sh|bash|-c) shift ;;
      *=*) shift ;;
      *) break ;;
    esac
  done
  tok="${1:-}"
  [ -n "$tok" ] && printf '%s\n' "$tok"
}

# 反查真实安装位置 + 归属
locate() {
  local exe="$1" real pkg
  case "$exe" in
    /*) real=$(readlink -f "$exe" 2>/dev/null || printf '%s' "$exe") ;;
    *)  real=$(command -v "$exe" 2>/dev/null); [ -n "$real" ] && real=$(readlink -f "$real") ;;
  esac
  [ -z "${real:-}" ] && { printf '未找到 (命令不在 PATH 里)\n'; return; }

  printf '%s' "$real"
  case "$real" in
    /snap/*) printf '   [snap: %s]\n' "$(printf '%s' "$real" | cut -d/ -f3)"; return ;;
    /var/lib/flatpak/*|"$HOME"/.local/share/flatpak/*)
      printf '   [flatpak]\n'; return ;;
  esac
  pkg=$(dpkg -S "$real" 2>/dev/null | cut -d: -f1)
  if [ -n "$pkg" ]; then
    printf '   [包: %s]\n' "$pkg"
  else
    printf '   [手动安装 / 非 dpkg 管理]\n'
  fi
}

found=0
printf '%-30s %s\n' "应用名" "安装位置"
printf '%s\n' "-------------------------------------------------------------------------------"

for dir in "${DESKTOP_DIRS[@]}"; do
  [ -d "$dir" ] || continue
  for f in "$dir"/*.desktop; do
    [ -f "$f" ] || continue
    # 跳过不出现在网格里的条目
    grep -q '^NoDisplay=true' "$f" && continue
    [ "$(grep -m1 '^Type=' "$f" | cut -d= -f2)" = "Application" ] || continue

    # 只取 [Desktop Entry] 主段, 避免抓到 [Desktop Action xxx] 里的 Name/Exec
    section=$(awk '/^\[Desktop Entry\]/{f=1;next} /^\[/{f=0} f' "$f")

    name=$(printf '%s\n' "$section" | grep -m1 '^Name\[zh_CN\]=' | cut -d= -f2-)
    [ -n "$name" ] || name=$(printf '%s\n' "$section" | grep -m1 '^Name=' | cut -d= -f2-)
    exec_line=$(printf '%s\n' "$section" | grep -m1 '^Exec=' | cut -d= -f2-)
    [ -n "$name" ] || continue
    [ -n "$exec_line" ] || continue

    # 过滤
    if [ -n "$QUERY" ]; then
      case "$(printf '%s %s %s' "$name" "$exec_line" "$f" | tr 'A-Z' 'a-z')" in
        *"$(printf '%s' "$QUERY" | tr 'A-Z' 'a-z')"*) ;;
        *) continue ;;
      esac
    fi
    found=1

    exe=$(parse_exec "$exec_line")
    printf '%-30s %s\n' "$name" "$(locate "$exe")"
    if [ "$VERBOSE" = 1 ]; then
      printf '%-30s   desktop: %s\n' "" "$f"
      printf '%-30s   Exec   : %s\n' "" "$exec_line"
    fi
  done
done

echo
if [ "$found" = 0 ]; then
  echo "没有匹配到应用。"
else
  echo "提示: 数据/配置目录通常在 ~/.config/<名字> 与 ~/.local/share/<名字>;"
  echo "      看某个包全部文件: dpkg -L <包名>"
fi
