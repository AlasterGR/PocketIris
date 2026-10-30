#!/data/data/com.termux/files/usr/bin/bash
# Put the chat server's data back from the newest backup in Documents/ca/backups (asks first; the current data is kept aside)
export PATH="$HOME/bin:$PATH"
dest="/sdcard/Documents/ca/backups"
newest=$(ls -1t "$dest"/pocketiris-data-*.tar.gz 2>/dev/null | head -1)
#  Stop when there is no backup to restore from.
[ -n "$newest" ] || { echo "No backup found in $dest. Run backup-data.sh first."; exit 1; }
echo "This will stop the server, move the current data aside, and restore it from:"
echo "  $newest"
read -r -p "Type yes to continue: " answer
#  Do nothing unless the answer was exactly yes.
[ "$answer" = "yes" ] || { echo "Cancelled. Nothing was changed."; exit 0; }
ca-stop
aside="$dest/replaced-by-restore-$(date +%Y%m%d-%H%M)"
mkdir -p "$aside"
#  Keep the current data, so that the restore itself can be undone.
mv "$HOME/.yep-anywhere" "$aside/" && echo "Current data moved to $aside"
cd "$HOME" && tar xzf "$newest" .yep-anywhere && echo "Data restored."
ca-start
