#!/data/data/com.termux/files/usr/bin/bash
# Show how much space the download caches and the main Termux folders use (read-only)
cd "$HOME"
du -sh .npm .cache 2>/dev/null
echo "--- inside .cache"
du -sh .cache/* 2>/dev/null | sort -h | tail -8
echo "--- main folders"
du -sh .local .claude ya-android 2>/dev/null
df -h "$HOME" | tail -1
