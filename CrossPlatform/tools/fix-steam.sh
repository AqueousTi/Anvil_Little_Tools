#!/usr/bin/env bash
# fix-steam.sh — 修复 Ubuntu 24.04 上 Steam 打不开的两个独立问题
#
#   问题 1: AppArmor 限制了 unprivileged user namespace
#           -> Steam 启动时的 bwrap 报 "setting up uid map: Permission denied"
#   问题 2: NVIDIA 内核模块(580.173.02) 与用户态库(580.178.04) 版本不一致
#           -> Steam 客户端加载 libGLX_nvidia 时段错误崩溃
#           根因: apt 升级到内核 7.0.0-34 时缺 linux-headers-7.0.0-34-generic,
#                 导致 objtool 缺失、NVIDIA 模块没编译出来
#
# 用法:  sudo bash fix-steam.sh
#        或普通用户运行(需要免密 sudo)
set -u -o pipefail

log() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
ok()  { printf '\033[1;32m  [OK] %s\033[0m\n' "$*"; }
bad() { printf '\033[1;31m  [!!] %s\033[0m\n' "$*"; }

TARGET_KERNEL=7.0.0-34-generic

# ---------- 0. 权限 ----------
if [ "$(id -u)" -ne 0 ]; then
  if sudo -n true 2>/dev/null; then
    SUDO="sudo -n"
  else
    bad "需要 root 权限, 且当前无法免密 sudo"
    echo "     请改用:  sudo bash $0"
    exit 2
  fi
else
  SUDO=""
fi
run() { if [ -n "$SUDO" ]; then $SUDO "$@"; else "$@"; fi; }

# ---------- 1. 补上缺失的内核头文件 (提供 objtool) ----------
log "步骤 1/5  安装 linux-headers-$TARGET_KERNEL"
if [ -e "/usr/src/linux-headers-$TARGET_KERNEL/tools/objtool/objtool" ]; then
  ok "已存在, 跳过"
else
  if run env DEBIAN_FRONTEND=noninteractive apt-get install -y "linux-headers-$TARGET_KERNEL"; then
    ok "安装完成"
  else
    bad "apt 安装失败 (继续后续步骤, 见上方输出)"
  fi
fi

# ---------- 2. 重新配置卡住的包 (会重建 NVIDIA 内核模块) ----------
log "步骤 2/5  dpkg --configure -a  (重建 NVIDIA 内核模块)"
run dpkg --configure -a || bad "仍有包未能配置成功, 见上方输出"

log "步骤 3/5  apt-get --fix-broken install"
run env DEBIAN_FRONTEND=noninteractive apt-get -f install -y || bad "修复依赖时出错"

# ---------- 4. 确认 NVIDIA 模块已为新内核生成 ----------
log "步骤 4/5  检查 $TARGET_KERNEL 的 NVIDIA 内核模块"
ko="/lib/modules/$TARGET_KERNEL/kernel/nvidia-580/nvidia.ko"
if [ -e "$ko" ]; then
  ok "已生成: $ko"
else
  bad "未生成: $ko"
fi
if dpkg-query -W -f='${Status}' linux-modules-nvidia-580-7.0.0-34-generic 2>/dev/null | grep -q 'install ok installed'; then
  ok "linux-modules-nvidia-580-7.0.0-34-generic 已配置"
else
  bad "linux-modules-nvidia-580-7.0.0-34-generic 仍未配置完成"
fi

# ---------- 5. 放开 AppArmor 的 user namespace 限制 ----------
log "步骤 5/5  放开 unprivileged user namespace"
conf=/etc/sysctl.d/60-userns.conf
echo 'kernel.apparmor_restrict_unprivileged_userns=0' | run tee "$conf" >/dev/null
run sysctl -q --system
val=$(sysctl -n kernel.apparmor_restrict_unprivileged_userns 2>/dev/null)
if [ "${val:-1}" = "0" ]; then
  ok "kernel.apparmor_restrict_unprivileged_userns = 0"
else
  bad "sysctl 当前值仍为 $val"
fi

# ---------- 验证 ----------
log "验证"
if [ "$(id -u)" -ne 0 ]; then
  if bwrap --unshare-user --uid 0 --gid 0 --ro-bind / / true 2>/dev/null; then
    ok "bwrap 沙箱测试通过 (Steam 的 user namespace 问题已解决)"
  else
    bad "bwrap 仍失败: $(bwrap --unshare-user --uid 0 --gid 0 --ro-bind / / true 2>&1)"
  fi
else
  echo "  (以 root 运行, 跳过 bwrap 测试 — 请以普通用户再测一次)"
fi

echo
echo "  运行中的内核      : $(uname -r)"
echo "  NVIDIA 内核模块   : $(sed -n 's/.*Kernel Module *//p' /proc/driver/nvidia/version 2>/dev/null)"
echo "  NVIDIA 用户态库   : $(dpkg-query -W -f='${Version}' libnvidia-gl-580 2>/dev/null)"
echo
echo ">>> 内核模块要重启后才会换成与用户态一致的版本, 请执行:  sudo reboot"
echo ">>> 重启后自检:  uname -r && nvidia-smi && cat /proc/driver/nvidia/version"
