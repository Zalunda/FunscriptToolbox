#!/usr/bin/env bash
# ScriptVersion:1.1

echo "How to use:"
echo "1. Move new-scene.mp4 and new-scene.funscript to this folder."
echo "2. Start this script."
echo "3. It will extract the audio from new-scene.mp4 and add the audio signature to the new-scene.funscript."
echo "4. It will allow you to synchronize your .funscript to a different version of the video"
echo "   (with audiosync.createfunscript or audiosync.verifyfunscript)."
echo ""

export PATH="[[FunscriptToolboxFolder]]:$PATH"

FunscriptToolbox
echo ""
echo ""
bash
