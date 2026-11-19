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
		--ui 5055 \
		"*.mp4" "*.vseq"

read -p "Press [Enter] to continue..."
