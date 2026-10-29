# FunscriptToolbox - Architecture & Tools Overview

`FunscriptToolbox` is a modular suite of .NET / C# command-line tools, libraries, and WPF GUI utilities designed for funscript authoring, audio-based synchronization, video retiming, automated AI-powered subtitle generation/translation, and motion-vector assisted tracking in OpenFunscripter (OFS).

---

## Solution Projects Overview

| Project | Type | Purpose |
| :--- | :--- | :--- |
| **`FunscriptToolbox`** | C# Console App | Main CLI entry point exposing all verb-based tools and commands. |
| **`FunscriptToolbox.Core`** | C# Class Library | Domain models and file format parsers (`Funscript`, `SubtitleFile`, `WIPFunscriptActionCollection`, etc.). |
| **`AudioSynchronization`** | C# Class Library | Acoustic fingerprinting and waveform cross-correlation engine for timeline alignment. |
| **`MotionVectorsExtractor`** | C++ Native Project | Native high-performance motion vector decoder/extractor interfacing with FFmpeg. |
| **`FunscriptToolbox.UI`** | WPF Application / Library | Interactive UI utilities (Speaker Identification Correction UI). |

---

## CLI Tools & Verbs Reference

All tools are accessible via `FunscriptToolbox.exe <verb> [options]`.

### 1. Installation & Environment Setup
* **`installation`** *(alias: `inst`)*
  - Scaffolds ready-to-run workflow folders populated with helper batch scripts and configuration templates:
    - `FSTB-CreateSubtitles2025/`: Subtitle transcription and translation workflows (`Staging`, `ManualHQWorkflow`, `AutomaticHQWorkflow`).
    - `FSTB-PrepareScriptForRelease/`: Script packaging and signature generation for releases.
    - `FSTB-VerifyDownloadedScripts/`: Automated verification of funscripts against local video files.

---

### 2. Audio Synchronization (`audiosync`)
Enables cross-release synchronization and drift detection by generating and comparing acoustic fingerprints (`.asig` files).

* **`audiosync.createaudiosignature`** *(alias: `as.cas`)*
  - Extracts the audio waveform from a video and generates an audio signature file (`.asig`), or embeds the signature directly into an existing `.funscript`.
  - Supports batch processing and recursive directory scanning (`-r`).

* **`audiosync.createfunscript`** *(alias: `as.cfs`)*
  - Re-synchronizes a funscript created for one video release/cut to match a different target video release using their respective audio signatures.
  - Allows exporting an offset timeline mapping (`--dumpoffsets` -> `.offsets.json`).

* **`audiosync.verifyfunscript`** *(alias: `as.vfs`)*
  - Compares the embedded audio signature in a `.funscript` against a local video file to verify synchronization.
  - With `--fix`, automatically shifts/adjusts the actions to realign the funscript with the video.

---

### 3. Video Retimer (`retimer`)
Automates variable-speed video compression (e.g. fast-forwarding through dead time or non-action segments while preserving normal speed during dialogue or funscripted actions).

* **`retimer.generatecontrol`** *(alias: `retimer.gen`)*
  - Analyzes existing subtitles (`.srt`) and funscripts (`.funscript`) alongside a video to generate a `.control.srt` timeline blueprint.
  - Detects speech density, isolated utterances, and gap thresholds to mark sections as controlled (normal playback) or gap/fast-forward.

* **`retimer.rendervideo`** *(alias: `retimer.render`)*
  - Uses FFmpeg to render the variable-speed video based on the `.control.srt` blueprint.
  - Produces the output retimed video (`.retimed.mp4`) and an exact time transformation mapping (`.offsets.json`).

* **`retimer.syncassets`** *(alias: `retimer.sync`)*
  - Uses the `.offsets.json` map to remap and warp all original sidecar files (`.srt`, `.funscript`) so they perfectly align with the new variable-speed retimed video.

---

### 4. Subtitles & AI Transcription Pipeline (`subtitles`)
A modular pipeline driven by JSON/C# configuration files to transcribe, identify speakers, and translate video audio into clean `.srt` subtitles.

* **`subtitles.create`** *(alias: `sub.create`)*
  - Processes single video files or multi-part video sequences (`.vseq` or `--autovseq`).
  - Executes a worker pipeline configured via `--config`:
    - **Audio Extraction**: Demuxes audio streams.
    - **VAD & Segmentation**: Voice Activity Detection to isolate dialogue chunks.
    - **Transcription**: Interfaces with Purfview Faster-Whisper, standalone Whisper, or LLM-based transcribers.
    - **Speaker Diarization / Identification**: Distinguishes dialogue by speaker.
    - **Translation**: Translates text using Google Translate API or AI/LLMs.
    - **Output Formatting**: Generates clean `.srt` subtitle files, cost reports, and audio signatures.

---

### 5. Motion Vectors (`motionvectors`)
> [!NOTE]
> Motion vector extraction is no longer supported directly in FunscriptToolbox. If you want to generate a video with embedded motion vector arrows, please use the customized FFmpeg release directly at [Zalunda FFmpeg Releases](https://github.com/Zalunda/FFmpeg/releases).

* **`motionvectors.prepare`** *(alias: `mvs.prep`)*: Displays a notification directing users to the custom FFmpeg release.

---

## Interactive GUI Tools (`FunscriptToolbox.UI`)

* **Speaker Correction Tool** (`SpeakerCorrectionTool.xaml`):
  - Interactive WPF dialog used during the subtitle creation workflow.
  - Allows human-in-the-loop validation: review transcribed lines, assign or rename speaker tags, and inspect per-speaker audio/statistics before translation and final subtitle export.

