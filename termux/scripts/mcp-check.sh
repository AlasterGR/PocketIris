#!/data/data/com.termux/files/usr/bin/bash
# Check that Claude can reach the app's tool server with the saved key (never prints the key)
export PATH="$HOME/bin:$PATH"
cd "$HOME"
echo "key file: $( [ -s "$HOME/.ca-mcp-token" ] && echo present || echo MISSING )"
echo "--- registration log"
tail -n 3 "$HOME/ca-mcp.log" 2>/dev/null
echo "--- what Claude reports (can take up to a minute)"
timeout 60 claude mcp list 2>&1 | grep -i -E "phone-browser|error"
echo "--- the full test of the tool server (answers Claude, refuses everyone else)"
bash "$HOME/bin/ca-selftest-mcp"
