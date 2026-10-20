#!/usr/bin/env bash
# Quick check of the browser bridge (same test as test-bridge.ps1 on Windows). TurboDL must be running.
PORT="${1:-9614}"
echo "--- GET /ping"
curl -s "http://127.0.0.1:$PORT/ping"; echo
echo "--- POST /add (opens the Add window with a small test file)"
curl -s -X POST "http://127.0.0.1:$PORT/add" \
  -H "Content-Type: application/json" -H "X-TurboDL-Client: test" \
  -d '{"url":"https://speed.hetzner.de/100MB.bin","silent":false}'; echo
echo "--- request from a web page origin (must be refused with 403)"
curl -s -o /dev/null -w "%{http_code}\n" -X POST "http://127.0.0.1:$PORT/add" \
  -H "Origin: https://example.com" -H "X-TurboDL-Client: x" -d '{"url":"https://example.com/a.zip"}'
