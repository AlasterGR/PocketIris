#!/data/data/com.termux/files/usr/bin/bash
# Save the chat server's data and the main settings into a dated archive in Documents/ca/backups
set -u
dest="/sdcard/Documents/ca/backups"
file="$dest/pocketiris-data-$(date +%Y%m%d-%H%M).tar.gz"
mkdir -p "$dest"
cd "$HOME"
#  Pack the data and the settings that cannot be rebuilt; the program files are left out because they can be reinstalled.
tar czf "$file" .yep-anywhere .claude.json .claude/settings.json .termux/boot .termux/termux.properties bin ya-android/package.json ya-android/package-lock.json .bashrc 2>&1
#  Report the result, or say that the archive was not made.
if [ -s "$file" ]; then ls -la "$file"; else echo "The backup was not made."; fi
