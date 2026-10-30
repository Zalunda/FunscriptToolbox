#!/usr/bin/env bash
# ScriptVersion:2.1

export PATH="[[FunscriptToolboxFolder]]:$PATH"

echo "--- subtitles.create ---"
FunscriptToolbox \
		subtitles.create \
		--config "./--FSTB-SubtitleGenerator.config" \
		--recursive \
		--verbose \
		--autovseq \
		"*.mp4" "*.vseq"

read -p "Press [Enter] to continue..."
