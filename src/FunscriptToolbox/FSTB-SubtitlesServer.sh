#!/usr/bin/env bash
# Runs FunscriptToolbox as a subtitles server (web UI on port 5055, reachable from the local network), with a console.
# Open http://<name or address of this machine>:5055 in a browser (on this machine: http://localhost:5055).
# If a firewall is active on this machine, the port 5055 (TCP) must be opened for the local network.
# Warning: there is no password, use it only on a trusted network.
# (to start it: 'bash FSTB-SubtitlesServer.sh', or 'chmod +x FSTB-SubtitlesServer.sh' once, then './FSTB-SubtitlesServer.sh')

# [TO CHANGE] The folder of your subtitles configuration (the one with --FSTB-SubtitleGenerator.config, ex. the
# 'FSTB-CreateSubtitles2025' folder created by the installation). Ex.
# WORK_FOLDER="$HOME/Videos/FSTB-CreateSubtitles2025"
WORK_FOLDER=""
CONFIG="--FSTB-SubtitleGenerator.config"

SCRIPT_FOLDER="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ -z "$WORK_FOLDER" ]; then
    echo "[ERROR] WORK_FOLDER is not set: edit \"${BASH_SOURCE[0]}\" and set WORK_FOLDER to the folder of your subtitles configuration."
    read -p "Press [Enter] to continue..."
    exit 1
fi
if ! cd "$WORK_FOLDER"; then
    echo "[ERROR] The folder \"$WORK_FOLDER\" doesn't exist."
    read -p "Press [Enter] to continue..."
    exit 1
fi
if [ ! -f "$CONFIG" ]; then
    echo "[ERROR] \"$CONFIG\" not found in \"$WORK_FOLDER\"."
    read -p "Press [Enter] to continue..."
    exit 1
fi

"$SCRIPT_FOLDER/FunscriptToolbox" \
		subtitles.create \
		--config "$CONFIG" \
		--recursive \
		--verbose \
		--autovseq \
		--runasserver '*:5055' \
		"*.mp4" "*.vseq"

read -p "Press [Enter] to continue..."
