#!/data/data/com.termux/files/usr/bin/bash
# Show the versions of everything PocketIris depends on (read-only)
export PATH="$HOME/bin:$PATH"
echo "claude:  $(claude --version 2>&1 | head -1)"
echo "node:    $(node -v)"
echo "npm:     $(npm -v)"
echo "ttyd:    $(ttyd --version 2>&1 | head -1)"
echo "openssh: $(ssh -V 2>&1)"
#  Say whether the secure shell server is running, because the PC can only use it while it is.
echo "sshd running: $(pgrep sshd >/dev/null && echo yes || echo no)"
ca-status
#  List the Termux packages that matter, when the package tool can be asked.
pkg list-installed 2>/dev/null | grep -E '^(nodejs|ttyd|openssh|termux-api|termux-tools)/'
echo "cpu: $(uname -m)"
