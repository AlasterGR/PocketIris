#!/data/data/com.termux/files/usr/bin/bash
# Trim the log files to the size and age limits in settings.conf, and show what was done
export PATH="$HOME/bin:$PATH"
conf=/sdcard/Documents/ca/settings.conf
echo "Limits come from $conf:"
#  Show the saved limits, or say that the defaults are used.
if [ -r "$conf" ]; then grep -E '^LOG_MAX' "$conf"; else echo "(no settings file yet: 5 MB and 180 days are used)"; fi
echo
ca-trim-logs -v
echo
echo "To change the limits, edit LOG_MAX_MB and LOG_MAX_DAYS in $conf."
