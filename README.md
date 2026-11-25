# FunscriptToolbox

A modular suite of .NET / C# command-line tools and a web UI designed for funscript authoring, acoustic synchronization, variable-speed video retiming, and AI-powered subtitle generation and translation.

---

## Table of Contents

- [Features Overview](#features-overview)
  - [1. Installation & OFS Integration](#1-installation--ofs-integration)
  - [2. Audio Synchronization (`audiosync`)](#2-audio-synchronization-audiosync)
  - [3. Smart Video Retimer (`retimer`)](#3-smart-video-retimer-retimer)
  - [4. AI Subtitle Generation & Translation (`subtitles`)](#4-ai-subtitle-generation--translation-subtitles)
- [Quick Start](#quick-start)
- [Solution Architecture](#solution-architecture)
- [Building from Source](#building-from-source)
  - [Prerequisites](#prerequisites)
  - [One-Click Local Build](#one-click-local-build)
  - [Command-Line Build](#command-line-build)
- [Automated Releases (CI/CD)](#automated-releases-cicd)
- [License](#license)

---

## Features Overview

### 1. Installation & OFS Integration
```cmd
FunscriptToolbox.exe installation
```
* Scaffolds ready-to-run workflow folders populated with helper batch scripts and configuration templates:
  * `FSTB-CreateSubtitles2025/`: Subtitle transcription, diarization, and translation workflows (`Staging`, `ManualHQWorkflow`, `AutomaticHQWorkflow`).
  * `FSTB-PrepareScriptForRelease/`: Script packaging and signature generation for releases.
  * `FSTB-VerifyDownloadedScripts/`: Automated verification of funscripts against local video files.

---

### 2. Audio Synchronization (`audiosync`)
Enables cross-release synchronization and drift detection by generating and comparing acoustic fingerprints (`.asig` files).

* **`audiosync.createaudiosignature`** *(alias: `as.cas`)*: Extracts audio waveforms from video files and produces `.asig` signatures (or embeds signatures directly into `.funscript` metadata). Supports batch processing and recursive scanning.
* **`audiosync.createfunscript`** *(alias: `as.cfs`)*: Re-aligns a funscript made for one video release/cut to match an alternate video cut using their respective acoustic signatures. Can dump full offset timeline mappings (`.offsets.json`).
* **`audiosync.verifyfunscript`** *(alias: `as.vfs`)*: Verifies sync between an embedded audio signature and a target video file. With `--fix`, automatically shifts actions to realign the funscript.

---

### 3. Smart Video Retimer (`retimer`)
Automates variable-speed video compression—speeding through dead time or non-action scenes while preserving normal playback speed during dialogue or funscripted actions.

* **`retimer.generatecontrol`** *(alias: `retimer.gen`)*: Scans subtitles (`.srt`) and funscripts (`.funscript`) alongside a video to generate a `.control.srt` timeline blueprint with speed changes.
* **`retimer.rendervideo`** *(alias: `retimer.render`)*: Uses FFmpeg to render the variable-speed video (`.retimed.mp4`) and an exact time transformation mapping (`.offsets.json`).
* **`retimer.syncassets`** *(alias: `retimer.sync`)*: Warps and remaps original sidecar files (`.srt`, `.funscript`) using `.offsets.json` so they match the newly retimed video.

---

### 4. AI Subtitle Generation & Translation (`subtitles`)
A modular pipeline to extract dialogue, perform voice activity detection, transcribe, identify speakers, and translate subtitles into clean `.srt` files.

* **`subtitles.create`** *(alias: `sub.create`)*:
  * **Audio Demuxing**: Demuxes audio streams from video or multi-part video sequences (`.vseq`).
  * **VAD & Segmentation**: Silero VAD isolates spoken dialogue chunks.
  * **Transcription**: Powered by Purfview Faster-Whisper, standalone Whisper, or LLM-based speech transcribers.
  * **Speaker Diarization**: Separates and attributes dialogue across multiple speakers.
  * **Translation**: Translates dialogue via Google Translate API or AI/LLMs.
  * **Web UI**: progress of all the scenes on a web page, and the Review editor for the human-in-the-loop review (timings, review warnings, speakers). `FSTB-SubtitlesServer.bat` / `.sh` (next to `FunscriptToolbox.exe`) runs it as a server, reachable from the other machines of the local network.

---


## Quick Start

1. Download the latest `FunscriptToolbox-<version>.zip` from the [Releases](https://github.com/Zalunda/FunscriptToolbox/releases) page.
2. Extract the archive into a directory of your choice.
3. Run `--FSTB-Installation.bat` (or open a command prompt and run `FunscriptToolbox.exe installation`).
4. The tool will set up ready-to-use workflow folders with batch templates.

---

## Solution Architecture

```text
FunscriptToolbox/
├── CreateRelease.bat              # Local release build & packaging script
├── PROJECT.md                     # Comprehensive architecture and CLI reference
├── .github/workflows/build.yml    # Automated CI/CD release workflow
├── scripts/
│   ├── set-version.ps1            # Stamps version numbers into AssemblyInfo.cs
│   └── package-release.ps1        # Builds staging directory and creates .zip
├── lib/
│   ├── ffmpeg-master-latest-...   # Bundled FFmpeg C++ headers, libs, and runtime DLLs
│   └── silero-vad/                # Silero Voice Activity Detection Python scripts
└── src/
    ├── FunscriptToolbox.sln       # Main Visual Studio Solution
    ├── FunscriptToolbox/          # CLI Application & verb implementations (.NET 10)
    ├── FunscriptToolbox.Core/     # Domain models & parsers (.NET 10)
    └── AudioSynchronization/      # Acoustic fingerprinting & sync engine (.NET 10)
```

| Project | Language / Framework | Description |
| :--- | :--- | :--- |
| **`FunscriptToolbox`** | C# / .NET 10 | Main console executable exposing all CLI verbs. |
| **`FunscriptToolbox.Core`** | C# / .NET 10 | Core domain models, `.funscript` and `.srt` parsers. |
| **`AudioSynchronization`** | C# / .NET 10 | Acoustic waveform fingerprinting (`.asig`) & cross-correlation. |

---

## Building from Source

### Prerequisites
* **Windows 10/11** (64-bit)
* **Visual Studio 2022** (Community, Professional, or Enterprise) with workloads:
  * **.NET desktop development** (.NET Framework 4.8 Developer Pack)
  
### One-Click Local Build
To build the complete solution and produce the release `.zip`:
1. Double-click **`CreateRelease.bat`** at the repository root.
2. The script will automatically locate MSBuild, compile the solution in `Release` configuration, and package the release archive into `dist\FunscriptToolbox-<version>.zip`.

To specify a custom version for the build:
```cmd
CreateRelease.bat 2.3.0
```

### Command-Line Build
Alternatively, using standard MSBuild commands:

```powershell
# 1. Restore NuGet packages
nuget restore src\FunscriptToolbox.sln

# 2. Build Solution (Release | Any CPU builds C++ x64 + C#)
msbuild src\FunscriptToolbox.sln /p:Configuration=Release /p:Platform="Any CPU" /m

# 3. Package release archive
powershell -ExecutionPolicy Bypass -File scripts\package-release.ps1 -Configuration Release
```

The resulting package will be generated at `dist\FunscriptToolbox-<version>.zip`.

---

## Automated Releases (CI/CD)

Releases are fully automated via GitHub Actions ([.github/workflows/build.yml](.github/workflows/build.yml)):

* **Trigger**: Automatically runs whenever a git tag is created (e.g. `2.3.0` or `v2.3.0`).
* **Version Stamping**: Automatically updates `AssemblyVersion`, `AssemblyFileVersion`, and `AssemblyInformationalVersion` from the tag.
* **Compilation**: Builds the solution on a Windows runner.
* **Release Publication**: Packages `FunscriptToolbox-<version>.zip` and attaches it directly to the published GitHub Release.

To release a new version from your terminal:
```bash
git tag 2.3.0
git push origin 2.3.0
```

---

## License

This project is licensed under the terms of the GNU General Public License v3.0 ([LICENSE](LICENSE)).
