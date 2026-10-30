#!/data/data/com.termux/files/usr/bin/bash
# Sample script: shows the phone's battery and free space (safe, read-only)
#  Show the battery details, or a note if the phone tools are not installed.
termux-battery-status 2>/dev/null || echo "(termux-api not available)"
df -h /sdcard | tail -1
