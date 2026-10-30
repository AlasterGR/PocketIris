#!/data/data/com.termux/files/usr/bin/bash
# Show whether the chat server and its helpers run, and the end of the server log (read-only)
export PATH="$HOME/bin:$PATH"
date
ca-status
echo "--- processes"
ps -ef | grep -E '[c]li.js|[t]tyd|[r]emote-control' | awk '{print $2, $8, $9, $10}' | head
echo "--- end of the server log"
tail -n 12 "$HOME/ya-android/server.log" | cut -c1-160
echo "--- end of the update log"
tail -n 8 "$HOME/ca-update.log" 2>/dev/null
