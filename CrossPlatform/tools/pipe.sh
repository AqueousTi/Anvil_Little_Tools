#!/usr/bin/env bash
# Local test helper (dev-only): send one command to a running suite instance
# through the same named pipe the second-instance hand-off uses
# ($TMPDIR/CoreFxPipe_LittleTools.Assistant.Command.v1).
#
#   pipe.sh <TMPDIR> <command>      e.g. pipe.sh .tools/stkverify/tmp ToggleStock
set -uo pipefail
tmp="${1:?tmpdir}"; cmd="${2:?command}"
python3 - "$tmp/CoreFxPipe_LittleTools.Assistant.Command.v1" "$cmd" <<'PY'
import socket, sys
path, cmd = sys.argv[1], sys.argv[2]
s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
s.settimeout(5)
s.connect(path)
s.sendall((cmd + "\n").encode())
data = b""
while not data.endswith(b"\n") and len(data) < 64:
    chunk = s.recv(64)
    if not chunk: break
    data += chunk
print("reply_pid=" + data.decode(errors="replace").strip())
PY
