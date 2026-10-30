#!/data/data/com.termux/files/usr/bin/bash
# Delete the npm and pnpm download caches (they fill up again by themselves, so this is safe)
cd "$HOME"
before=$(du -sm .npm .cache 2>/dev/null | awk '{s+=$1} END {print s}')
rm -rf .npm/_cacache .cache/pnpm .cache/pnpm-*locks*
after=$(du -sm .npm .cache 2>/dev/null | awk '{s+=$1} END {print s}')
echo "caches: ${before} MB before, ${after} MB after"
