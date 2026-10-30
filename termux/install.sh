#!/data/data/com.termux/files/usr/bin/bash
# Install the ca-* commands, allow the app to run commands, start the server at boot,
# (the app's tool server is registered with Claude by ca-start, with the app's secret key). Safe to run again.
mkdir -p "$HOME/bin" "$HOME/.termux/boot" "$HOME/ya-android" "$HOME/.yep-anywhere"
#  Copy every ca-* command into ~/bin, copying first and renaming after, so a running script is never overwritten in place.
#  Go through every ca-* command file and install it.
for f in /sdcard/Documents/ca/ca-*; do
  b=$(basename "$f"); cp "$f" "$HOME/bin/.$b.new" && chmod +x "$HOME/bin/.$b.new" && mv -f "$HOME/bin/.$b.new" "$HOME/bin/$b"
done
touch "$HOME/.termux/termux.properties"
#  Let apps ask Termux to run commands, unless that is already switched on.
grep -q '^allow-external-apps *= *true' "$HOME/.termux/termux.properties" || echo 'allow-external-apps=true' >> "$HOME/.termux/termux.properties"
termux-reload-settings 2>/dev/null
cat > "$HOME/.termux/boot/claude-android" <<'BOOT'
#!/data/data/com.termux/files/usr/bin/bash
termux-wake-lock
"$HOME/bin/ca-start"
BOOT
chmod +x "$HOME/.termux/boot/claude-android"
#  Create the settings file the scripts read, unless it exists already (an edited one is never overwritten).
if [ ! -f /sdcard/Documents/ca/settings.conf ]; then
  cat > /sdcard/Documents/ca/settings.conf <<'CONF'
# PocketIris settings read by the scripts in Termux. Edit the numbers with any text editor; no restart is needed.
# Log files are cut back to this many megabytes (the newest part is kept):
LOG_MAX_MB=5
# Log files that nothing has written to for this many days are deleted (180 days is about six months):
LOG_MAX_DAYS=180
CONF
  echo "Created /sdcard/Documents/ca/settings.conf with the default limits"
fi
#  The tool-server registration needs the app's secret key, so ca-start does it (the app passes the key each time it starts the server).
echo "Installed commands: $(ls $HOME/bin | grep '^ca-' | tr '\n' ' ')"
