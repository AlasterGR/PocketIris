#!/data/data/com.termux/files/usr/bin/bash
# Upgrade Termux packages deliberately: save a list of what is installed first, show what would change, ask, then check health
export PATH="$HOME/bin:$PATH"
stamp=$(date +%Y%m%d-%H%M)
dir=/sdcard/_Review/backups
before="$dir/pkg-before-$stamp.txt"
mkdir -p "$dir"

#  The snapshot comes first, so it is always known what the phone had before any upgrade.
dpkg-query -W -f='${Package}=${Version}\n' > "$before"
echo "Saved the list of installed packages: $before ($(wc -l < "$before") packages)"
echo
echo "Refreshing the package list (downloads only the list, installs nothing)..."
pkg update > /dev/null 2>&1
apt list --upgradable 2>/dev/null | grep upgradable | sed 's/^/  /'
count=$(apt list --upgradable 2>/dev/null | grep -c upgradable)
#  Nothing waiting means nothing to do.
if [ "$count" -eq 0 ]; then echo "Nothing to upgrade."; exit 0; fi

#  Claude Code runs as a patched glibc program; glibc packages changing under it is the one upgrade that can break it.
if apt list --upgradable 2>/dev/null | grep -qi glibc; then
  echo
  echo "WARNING: glibc packages are in the list. Claude Code depends on them; upgrade only after asking Claude on the PC."
fi
echo
read -r -p "Upgrade these $count packages now? Type yes to go on: " answer
#  Anything but "yes" stops here with nothing changed.
if [ "$answer" != "yes" ]; then echo "Stopped; nothing was changed."; exit 0; fi

#  Keep the existing settings files when a package ships new ones, so the upgrade never stops to ask in the middle.
pkg upgrade -y -o Dpkg::Options::=--force-confold
after="$dir/pkg-after-$stamp.txt"
dpkg-query -W -f='${Package}=${Version}\n' > "$after"
echo
echo "What changed (before -> after):"
diff "$before" "$after" | grep '^[<>]' | sed 's/^</  was:/; s/^>/  now:/'
echo
ca-health
echo
echo "Next: run tools\\smoke-test.ps1 on the PC. When it passes, update compat.json there. To restart the server: ca-restart."
