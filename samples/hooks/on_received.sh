#!/bin/sh
# Example hook: copies every received file to an inbox directory named after the partner
# and logs the event. Configure it with Hooks__OnReceived=/path/to/on_received.sh
# All parameters are available as OFTP_* environment variables and as JSON on standard input.
set -eu

inbox="${INBOX_DIR:-/data/inbox}/${OFTP_PARTNER_SSID}"
mkdir -p "$inbox"
cp "$OFTP_FILE_PATH" "$inbox/${OFTP_VIRTUAL_FILE_NAME}_${OFTP_FILE_DATE}${OFTP_FILE_TIME}"

# Standard output is written to the application log.
echo "Copied $OFTP_VIRTUAL_FILE_NAME ($OFTP_FILE_SIZE bytes) from $OFTP_PARTNER_NAME to $inbox"
