# Timing Refinement Ideas — Suggestions from Gemini Neon (Nov 2026)

Context: Based on `GeminiDecisionProbes.md` and `TimingRefineEngineStartEndJudge.cs` (benchmark: `HNVR-141` parts A & B).

## Why Previous Probes Hit a Ceiling

1. **Confirmation bias when giving the expected text to an audio model.** In Probe 2, `P(complete)` stayed high even 400 ms inside speech. When an LLM is told *"check if `ki-mo-chi-i` is here"*, its audio encoder hallucinates the missing edge syllable.
2. **LLMs are bad at cross-clip acoustic subtraction, but great at sequential transcription.** Comparing 4 overlapping clips that differ by 200 ms fights the model's architecture (strong order bias, mushy 0.2–0.8 probabilities, Probe 12 barely listening to audio).
3. **Dropped edge words break text-anchored timing.** Probe 14 (all) showed that the AI accurately found `Yo-i-sho` / `Ja-ma` / `Sa-kki`, which looked like 0.9–2.3 s Start cuts only because the full-audio transcript dropped `ねえ` / `あー！` / `んー`.

Below are **5 approaches** that stop asking models to judge cut clips, and instead play to what **Flash**, **Neon**, and **Decision** are actually good at.

---

## 1. In-Audio Spoken Markers + Re-transcription (Flash → Neon) — *Highest upside*

**Core idea:** Instead of slicing into separate clips or asking the model to guess timestamps, **splice short, distinct spoken markers directly into the audio stream at every candidate terrain dip**, then ask the model to transcribe what it hears *with the markers inline*.

* **How it works:**
  1. Take a ~15–20 s chunk of audio (covering ~5–8 subtitles) and identify the candidate terrain dips (e.g., merging dips closer than ~150 ms so there are ~12–20 markers per chunk).
  2. At each dip, **insert** a short (~150–200 ms), low-pitch English/synthetic spoken marker (`"1"`, `"2"`, `"3"`, ... or `"A"`, `"B"`, `"C"`, ... — acoustically unmistakable from the Japanese speaker) into the timeline.
  3. Send that **one continuous clip** to **Flash** (test blind first, without expected text) and ask it to transcribe sequentially, keeping markers where they fall:
     ```text
     [1] (breath) [2] ねえ、よいしょ [3] [4] さっきの気持ちよかった [5] (moan) [6]
     ```
  4. Map each marker back to its exact original terrain dip timestamp.
* **Why this bypasses previous failures:**
  * Uses the model's #1 audio strength (**sequential ASR**), with zero internal-clock drift and zero multi-clip comparison.
  * Solves the **global assignment problem** (a blob between line `i` and line `i+1` is naturally placed with the right line, or tagged `(breath)`/`(moan)`).
  * Automatically catches **dropped leading interjections** (`ねえ`, `あー！`, `んー`).
* **Confidence / Auto-validation:**
  * When **Marker-Flash** lands on the exact same Start & End dips as **Algorithmic Snap** (within tolerance), two independent methods agree.
  * Escalate only chunks with disagreements (or dense/breathy speech) to **Neon** (a 30-min video is ~90 chunks total; escalating 30% is only ~25–30 Neon requests).

---

## 2. "Blind Clip Transcription" + Text-Only Comparison (Flash + Decision/Neon)

**Core idea:** Eliminate confirmation bias completely by **never giving the audio model the expected text**.

* **How it works:**
  1. **Flash (audio, blind):** Give Flash *only* the snapped clip `[Start..End]` (normalized, padded with 300 ms stolen silence) and ask it to transcribe *exactly* what is spoken in syllabled romaji, plus flag if the clip sounds abruptly cut off or has leading/trailing non-speech (`[breath]`, `[moan]`).
  2. **Decision or Neon (text-only):** Compare `Full-AI romaji` vs `Blind-Flash romaji` (solving the "too complex in code" romaji comparison noted in `GeminiDecisionProbes.md`).
     * With **text-only Decision** (very cheap, deterministic, no audio fuzziness), ask 4 `noul` questions:
       * Are opening syllables missing in the clip?
       * Are closing syllables missing in the clip?
       * Are there extra words at the start?
       * Are there extra words at the end?
* **Why it works for auto-validation:**
  * If Start or End is cut into speech, Flash *cannot* hallucinate the missing syllable because it never saw the full transcript.
  * If Start or End is too wide and swallowed a neighbor's word or a moan, Flash transcribes that extra word/moan.
  * If blind Flash matches the full-AI romaji at both edges cleanly, you get a much stronger `CONFIDENT_GOOD` signal than Probe 9/14.

---

## 3. Text-Only "Physics + Terrain" Auditor (Neon, batched 40–50 subtitles/call)

**Core idea:** Probe 12 showed that `desc_only` (no audio, just terrain zone numbers) already fixed ~half of the too-wide Ends. Japanese is **mora-timed** (~120–160 ms/mora), so many timing errors are visible **without audio at all**.

* **How it works:**
  1. In code, compute per-subtitle mora count from the romaji and the local speaking rate (ms/mora) from nearby "anchor" subtitles (those with >= 600 ms silence on both sides, which the terrain probe showed are 91% good).
  2. Send **Neon** a batch of 40–50 consecutive subtitles as pure text (~6–10 calls for an entire scene):
     * Subtitle romaji + mora count + expected duration range.
     * Snapped `[Start..End]` and its actual duration.
     * The list of terrain segments (`Speech` / `Breath` / `Click` / `Silence`, duration, peak dB, `LongestVoicedRun`) from `Start - 1.5s` to `End + 1.5s`.
  3. Ask Neon to:
     * Mark each Start/End as `CLEAN`, `TOO_WIDE` (e.g., 6-mora line spanning 2.5 s with a silence + second low-voicing blob at the end), `TOO_SHORT` (cut), or `AMBIGUOUS`.
     * Suggest the exact candidate dip for `TOO_WIDE` / `TOO_SHORT`.
* **Why it's useful:**
  * Almost free (text-only), sees the **whole sequence** of neighbors, and leverages Neon's Japanese linguistic knowledge (long vowels, geminates, trailing particles).
  * Acts as a fast triage layer so audio calls (Flash/Neon) are only spent on `AMBIGUOUS` or `TOO_WIDE`/`TOO_SHORT` boundaries.

---

## 4. Repurpose Decision for Single-Blob Classification (Not Clip Comparison)

The probes showed Decision can't reliably pick the best of 4 overlapping clips, **but** it *is* good at cheap, deterministic classification of a single normalized clip (sanity probe: speech vs silence).

* **What to try with Decision:**
  * **Adjacent-blob speech detector (no text):** For any boundary without >= 600 ms of clean silence outside it, extract *only* the adjacent terrain blob (right before Start, right after End, or the extra tail between `Init` and the next inward dip — similar to `TailPass` in `TimingRefineEngineStartEndJudge.cs`).
  * Ask Decision a single `choice` question *without the subtitle text*: `spoken Japanese words / moan or groan / breath or sigh / laugh / silence or noise`.
  * At $0.10/M input tokens, this gives a calibrated `P(speech)` for every suspicious neighbor blob — useful for catching systematic too-wide Ends that swallowed a moan/exhale, and confirming that a blob before Start is just a breath (not a dropped `ねえ`).

---

## 5. Combine Orthogonal Signals for Auto-Validation ("3-Way Agreement" Gate)

No single probe passed ~44% Starts / ~27% Ends confident without errors because each individual signal has blind spots. Instead of tuning one complex threshold on one model, auto-validate a boundary **only when independent signals agree**:

* **Auto-Accept Start** if:
  1. Terrain has clean silence before Start (e.g. >= 400–600 ms) **OR** Decision says the preceding blob is non-speech (`P(non-speech) >= 0.85`), **AND**
  2. Blind Flash (Idea 2) or Marker-Flash (Idea 1) agrees on the opening morae/dip, **AND**
  3. Neon's text-only mora-duration check (Idea 3) says `CLEAN`.
* **Auto-Accept End** if:
  1. Duration matches mora count (no unexplainable tail), **AND**
  2. Blind Flash / Marker-Flash agrees on the closing morae/dip, **AND**
  3. Either >= 400 ms silence after End, or Decision classifies the following blob as non-speech.

Because the failure modes of **terrain acoustics**, **mora-duration physics**, and **blind/marker ASR** are largely independent, their intersection should push auto-validated coverage much higher (targeting 60–80%+) while keeping false-confident cuts near zero.

---

## Suggested Order to Prototype (on `HNVR-141` A + B)

1. **Idea 1 — Spoken Markers with Flash on 10–15 chunks of Part A:** Splice pre-recorded ~180 ms `"1".."20"` WAVs into the terrain dips of 15-second chunks and run Flash. Single highest-leverage test because it attacks both **improving accuracy over Snap** and **auto-validation** at once.
2. **Idea 2 — Blind Flash + Text-Only Decision:** Easy to script on the 60 `"same"` + `"new"` subtitles; directly tests whether removing confirmation bias fixes the `CONFIDENT_BAD` rate.
3. **Idea 3 — Text-Only Neon Mora+Terrain Auditor:** Zero audio upload; fast to test on all 273 subtitles of Part A in ~6 requests.
