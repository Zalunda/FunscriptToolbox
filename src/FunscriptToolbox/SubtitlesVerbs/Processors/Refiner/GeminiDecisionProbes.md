# Gemini Decision probes for a timing refine engine (Nov 2026)

Experiments to see if `gemini-decision-1` (Decisions API) could be the base of a new `TimingRefineEngine`.
Goal: reduce, with a HIGH confidence, the number of timings the user has to validate. If ~70% still need a validation, the engine is useless.

Nothing was implemented in the code. The probe scripts (F# `.fsx`) were in a temporary scratchpad and may be gone; this file is the record.

## Setup

- Scene: `ZZZ-TestRefineTiming\[chubbybogs] HNVR-141 - Young face - Nurse`, parts A (273 subtitles) and B (254), validated `manual-input.srt` as the ground truth.
- 30 subtitles per part ("same", probes 2 to 11) + 30 others per part ("new", probes 6 to 11, used to check that a rule is not tuned on its own data).
- "Good" = from 300 ms outward to 100 ms inward (the user's priorities: Start matters more than End; cutting into the speech is worse than extra silence; ~300 ms of extra time is fine, 500+ ms is noticeable).
- Status per subtitle (in the probe `.srt` files): `CONFIDENT_GOOD` / `CONFIDENT_STARTONLY_GOOD` / `CONFIDENT_ENDONLY_GOOD` / `CONFIDENT_BAD` / `NOT_CONFIDENT_GOOD` / `NOT_CONFIDENT_BAD`. CONFIDENT = "would not ask the user to validate" (computed without the truth); GOOD/BAD = vs the ground truth.
- Clips: 16 kHz mono WAV, volume normalized (peak at 90%, gain <= 20x), real silence stolen from the terrain spliced against the boundary (`FindSilenceToSteal`), the subtitle text in the request.
- Price: $0.10 / M input tokens, no output tokens. All the probes together: about $1.10.
- Probe results written next to the videos: `HNVR-141-<part>.probe4.srt`, `.probe5.srt`, `.probe7.srt`, `.probe9.srt`, `.probe11.srt`.

## Facts about the Decision API

- Request: `POST https://generativelanguage.googleapis.com/v1beta/decisions`, `{model, input: [text/audio blocks], questions: {key: {type: noul|choice|score, instructions, criteria}}}`. Several audio blocks in one request work. A `choice` needs 2 to 255 options.
- **Deterministic**: the same request gives exactly the same probabilities (tested on 113 boundaries). There is no temperature. Repeating a request is useless.
- **The order of the clips biases the answer a lot** (widest clip first => it tends to pick the widest, and the reverse). Diversity can only come from different inputs: re-ordering the clips (rotations / shuffles) works well for that.
- Short raw clips (1 to 1.5 s, raw volume): the model is nearly blind. 3 s normalized clips: it hears speech vs silence (P(words) 0.60 vs 0.21) and compares two clips (P(same) 0.79 identical vs 0.31 different).
- Latency: median ~450 ms.

## The probes

| # | What | Result |
|---|---|---|
| 1 | No text: two clips meeting at the candidate ("is the start cut?"), and A/B comparison vs the widest clip (with/without the text). Raw volume. | No signal for the "cut" questions (P ~0.4 everywhere). A/B: weak signal. |
| sanity | Speech vs silence, identical vs different clips, 3 s, normalized. | Works (see above): normalization is needed. |
| 2 | The text + one clip per candidate, choice "heard complete / similar / start cut / end cut". | Signal (AUC Start 0.85, End 0.75) but P(complete) stays high even 400 ms inside the speech: a fixed threshold cannot place a boundary. |
| 3 | Same + padding with stolen silence; pairwise "does clip A lose part of the text vs REFERENCE". | Padding: no clear gain. Pairwise: similar signal. Found a bug of the probe (the "dips only" candidates were wrong); the terrain is fine: a dip is within ~40 ms (Start) / ~70 ms (End) of the validated time for 90% of the boundaries. |
| 4 | ONE request per boundary: the text + one clip per terrain dip (2 to 7), choice "which is the shortest clip that still contains the whole text?", 2 orders (widest first / tightest first) averaged. | First usable shape. Strong position bias (cancelled by averaging the 2 orders). Too-wide misses = moans/breaths before the words (told to be ignored) or another subtitle. |
| 5 | Probe 4 + neighbour limits + an "extra sound before the first word?" question per clip + a knockout variant (2 clips per request). | The "extra sound" question has no signal (~0.3 everywhere) and caused cuts: dropped. CONFIDENT = both picks agree + p >= 0.35 + margin >= 0.10 between the 2 best clips: no confident cut, ~27% of the boundaries confident. |
| 6 | Many variants on the same candidates: shuffled orders, a repeat, "edge" clips (~2 s around the boundary, first/last words only), another phrasing ("closest"), neighbour-words check. | Deterministic (the repeat = identical). The order changes the answer. Edge clips / neighbour check: no gain. Searching the best combination overfits (tuned on A, 1 to 3 confident-bad on B). |
| 7 | The clips in many orders (12 "shortest" + 6 "closest"); pick = argmax of the mean; confidence = share of the orders voting for the pick (within 100 ms). Tuned on "same", tested on "new". | Holds on the new subtitles: Start 36/115 confident (1 bad), End 31/110 (0 bad). 6 orders keep most of it (Start 35/115, End 23/110). ~12% of the subtitles fully confident. **But the candidates were dips within +/-1 s of the TRUTH: flattering.** |
| 8 | REALISTIC, like `TimingRefineEngineStartEndJudge`: from the preparer's initial snapped bounds ("Init" in the TimingTrace), all the dips within +/-2 s (neighbours' initial bounds as limits); pass 1 = K dips spread evenly, passes 2-3 = the dips around the winner; K requests per pass (cyclic rotations); K = 3 or 4. | The initial snapped bounds alone: Start 78% good, End 54%. The best dip of the window: 96% / 94%. **The AI search is worse than no AI**: Start 61% (K=4) / 70% (K=3), End ~50%, with misses of 1.5 to 2 s. Fewer clips per request is better. |
| 9 | Verification instead of search: the initial dip + its nearest neighbour on each side (dips closer than 80 ms merged), 3 clips, 3 rotations; move to the winner and repeat (max 4 steps). | CONFIDENT (initial dip kept, unanimous): Start 32/117 (2 bad, too wide 323/429 ms), End 31/118 (3 bad). The AI pick itself is not better than the initial boundary. |
| terrain | No AI: can the terrain say which initial boundaries are good? (all 527 subtitles) | Start: >= 600 ms of silence before => 91% good (~40% of the Starts); < 100 ms => 60%. End: < 300 ms of silence after => only 11-23% good; more => ~75%. Combined with probe 9 for Start ("AI kept" OR silence >= 1000 ms): ~44% of the Starts confident, ~3% bad (mostly mildly too wide). Not reliable for the End. |
| 10 | "Outside" check (user's idea): 1.2 s of audio outside each candidate (+300 ms of real silence against the cut), "does it contain part of the first/last words?", one request per dip. | Start: only cuts >= 300 ms are seen (P(speech) 0.37 vs ~0.18); 100-300 ms cuts are not. End: no signal (AUC 0.66). At most a safety net. |
| 11 | Like StartEndJudge: Start pass with every clip running to the MAX End (initial End + 2 s), then End pass with every clip starting at the LOCKED Start (variants: locked on the AI's Start / on the validated Start), the window capped by the next subtitle's locked Start. Probe 9's verification. | Start: 34/120 confident (2 small cuts). End (AI lock): 32/120 confident (1 bad). Locking on the validated Start does NOT help the End. ~13% of the subtitles fully confident. |
| 12 | "Labeled zones" (user's idea). Part A: the 26 subtitles the user marked "obviously bad SnapOnly" (`HNVR-141-A.snaponly50.srt`) + 10 perfect ones. The SnapOnly boundary +/-2 s cut at the terrain dips into zones A, B, C... (time order); the previous/next line texts given; ONE choice request "in which zone is the FIRST / LAST sound of the line?". Variants: `zones` (one audio block per zone, 300 ms of stolen silence in front), `zones_desc` (+ per zone: duration, peak dB vs the loudest zone, ms of sound detected), `clip_desc` (one clip, 300 ms of silence inserted at each cut, + the description with the times in the clip), `desc_only` (no audio: control). 288 requests, $0.02. | Good picks: Start (SnapOnly bad, n=9) zones 5 / zones_desc 2 / clip_desc 1 / desc_only 2; Start (SnapOnly good, n=27) 14 / 22 / 17 / 16; End (SnapOnly bad, n=23) 9 / 13 / 15 / 12; End (SnapOnly good, n=13) 5 / 8 / 7 / 7. **`desc_only` (no audio) is about as good as the variants with audio: the model mostly reads the numbers, it barely listens.** It fixes about half of the too-wide Ends, but it breaks good boundaries. CONFIDENT (zones_desc and clip_desc agree, both p >= 0.5): Start 10 (0 bad), End 9 (1 bad) of 36. |
| 13 | Romaji + independent yes/no questions (user's idea). Part A re-transcribed by Gemini 4 in syllabled romaji (`HNVR-141-A.Worker.full-ai-romaji.srt`), snapped with `TimingRefineEngineNone` (`...manual-input-preparer-romaji.srt`): its snapped bounds are the starting point. The same 36 subtitles as probe 12. Candidates: the snapped boundary + the dips within +/-1 s. ONE request per candidate (no order bias): CLIP IN (800 ms on the line's side) + CLIP OUT (800 ms on the other side), 300 ms of stolen silence at their far ends, 4 `noul` questions: is the first/last syllable of IN '<yo>'? is IN's edge cut in a syllable? are the neighbour line's syllables in IN? is a part of the line's first/last syllables in OUT? 236 requests, $0.013. | Separation of good vs bad candidates (AUC): first/last syllable 0.72 (Start) / 0.71 (End); "cut", "neighbour" ~0.55-0.69; the product 0.73 / 0.80. All the answers stay in 0.2-0.8 (the right candidate ~0.7-0.8, its neighbours ~0.6): no threshold isolates it. Argmax: Start 22/35 good, End 21/35 vs the romaji snap 25/35 and 22/35: **no gain, nothing confident.** |
| 14 | Probe 13 with **19 yes/no questions per request** (user's idea: "pack" the request): first syllable / first 2 / first 3 syllables / first word / first 2 words, "the syllable heard COMPLETE", "speech right at the edge (tight)", "any word in IN", "IN cut in a syllable / in a word", "breath or moan before the first word", "IN mostly silence", "first word different", "part of the line in OUT", "first word in OUT", "OUT cut in a word", "any word in OUT", "neighbour's syllables in IN / in OUT" (mirrored for the End). Score = the mean of the oriented answers (no tuning). Each question costs ~60 input tokens (1650 tokens per request vs 560): 236 requests, $0.039. | **Start: argmax 27/35 good (snap 25/35); CONFIDENT = the pick beats the 2nd candidate by >= 0.06: 13/35 (37%), 0 bad.** End: argmax 20/35 (snap 22/35), margin >= 0.06: 8 (1 bad); >= 0.08: 3 (0 bad). Best questions (AUC): Start first word 0.83, tight 0.81, "first word different" 0.80, syllables 0.77-0.79, complete 0.75; End complete 0.76, word in OUT 0.75, first word 0.75. Useless or inverted: breath/moan before the word (0.44 / 0.48), neighbour in OUT (0.50 / 0.35). The margin threshold is chosen on these 35 boundaries: to confirm on other subtitles. |
| 14 (all) | Probe 14 unchanged on ALL the subtitles of part A (1940 requests, $0.32); checked on the 233 subtitles NOT used to choose the rule. | **Does not hold.** Start: argmax 69% good vs the romaji snap 75% (worse than no AI); margin >= 0.06: 26% confident, 7 bad (11%); >= 0.08: 14%, 2 bad. End: argmax 59% vs snap 77%; margin >= 0.06: 24%, 8 bad. AUC per question similar to the first run (Start first word 0.77, tight 0.76; End complete 0.74). The 36 marked subtitles were mostly bad snaps: easy to beat. Most confident Start cuts (0.9 to 2.3 s): the romaji transcription DROPPED a leading interjection the truth keeps (#232 24:15.6 "ねえ、よいしょ" -> "Yo-i-sho...", #226 22:58.7 "あー！邪魔..." -> "Ja-ma...", #82 06:04.4 "んー、さっき..." -> "Sa-kki..."), or the line has it but the preamble "ignore moans" made the AI skip it (#205 18:04.4 "n-n ki-mo-chi-i"): the AI found the first TRANSCRIBED word. |
| romaji snap | Not an AI check: the snapped bounds of the romaji transcription vs the old (kanji) one, part A, all subtitles (vs the validated manual-input). | OLD (kanji): Start 80% good, End 56% (118 too wide), both 46%. **NEW (romaji): Start 74%, End 75% (62 too wide), both 55%.** The romaji transcription (311 lines, more finely split) gives much better Ends (the old manual-input was made from the old transcription, so the comparison even favours the old one). |

## Terrain: why the snapped Ends are too wide (no AI, Nov 2026)

- The transcription's End is ALWAYS late on the too-wide Ends (part A kanji: 116 too wide, the original End is 250 ms to 1.9 s after the validated End in all of them).
  It usually lands in the NEXT sound after a silence (85 / 116 inside a "Speech" piece): the snap ("inside a mountain => outward to the end of the piece")
  then extends the End to the end of that sound. The validated End is at a dip in 113 / 116 cases (a speech edge in 93).
- That next sound is very often a Speech piece with NO voicing: a breath promoted to "whisper" by `PromoteWhispers` (`MaxWhisperGap` = 300 ms:
  a breath up to 300 ms away from speech, not much quieter, becomes speech).
- Simulation of the snap (scratch scripts `terrainSim.fsx` / `knobs.fsx`, the baseline reproduces the romaji Init traces 230/230) with only the knob changed,
  on all the subtitles matched 1-to-1 (good = from 300 ms outward to 100 ms inward):

| Terrain knobs | A kanji: Start / End / both (cuts S, E) | B kanji | A romaji |
|---|---|---|---|
| default (`MaxWhisperGap` 300 ms) | 81 / 57 / 47% (14, 0) | 78 / 52 / 41% (27, 25) | 74 / 77 / 57% (9, 2) |
| `MaxWhisperGap` = 50 ms | 83 / 61 / 53% (17, 0) | 79 / 56 / 44% (28, 28) | 77 / 80 / 63% (12, 2) |
| **`MaxWhisperGap` = 25 ms** | **86 / 62 / 56% (17, 0)** | **79 / 56 / 44% (30, 28)** | **79 / 81 / 65% (12, 2)** |
| no whisper promotion at all | 86 / 63 / 55% (23, 9) | 80 / 60 / 48% (37, 40) | 82 / 80 / 66% (17, 11) |

  `WhisperRelativeDb` (0 or 3 dB) and the whisper clusters change little. No promotion at all gains a bit more but adds cuts (real devoiced / breathy
  syllables become silence: the terrain can't tell them from a breath). One scene only (HNVR-141): to confirm on another scene.

## Summary

- The terrain is good: the right dip is almost always in a +/-2 s window. The problem is to pick it.
- Decision can **confirm** a boundary (when all the clip orders agree, it is almost always right), but it confirms only ~25-35% of them, while ~78% of the initial Starts are already good. It cannot **find** the right dip when the initial one is wrong (its search is worse than no AI).
- Realistic best: ~44% of the Starts and ~27% of the Ends could skip the validation (a few % of errors, mostly too wide), ~13-14% of the subtitles fully. Far from the goal.
- Tuning many rules/variants on 30 subtitles overfits: always validate a rule on other subtitles.
- The probes that centered the candidates on the truth (+/-1 s) gave flattering numbers: test from the engine's real starting point (the initial snapped bounds).
- Gemini 4 Argon (Pro-like) was already tested by the user for timing checks: never good enough to skip the validation, and much more expensive.

## Ideas

Tried, not useful:
- Questions without the text (cut / same words).
- A fixed threshold on "heard complete".
- "Extra sound before the first word" (moan, breath...).
- "Does the clip contain words of the neighbour subtitle".
- Edge-only clips (~2 s around the boundary).
- "Outside the boundary" clip (only big Start cuts).
- Searching the whole +/-2 s window with Decision.

Not tried yet:
- **Romaji check** (user): transcribe in romaji, then ask Decision if the romaji could be the same audio (with a bad transcription). The user had tried a syllable-count comparison in code, abandoned because the comparison was too complex.
- **Fix the initial End snapping without AI**: the initial Ends with less than 300 ms of silence after them are wrong 77-89% of the time (mostly too wide); a terrain rule or an extra candidate there may help more than any AI question. In part A, 115 of the 268 SnapOnly Ends are bad, most of them 1.1 to 1.5 s too wide (a systematic pattern), and probe 12's `desc_only` (no audio, only the zone statistics) fixes about half of them: the information is in the terrain.
- Use Decision only to **order** the validations (worst first): the vote share separates good from bad with an AUC of ~0.70-0.75: an order, not a "stop here, the rest is fine".
- More clip orders only for the uncertain boundaries; pairwise requests between the two most-voted dips; extra candidates between the dips for the End.
- Use the confirmation (unanimous verification) as a safe auto-accept for the Starts, combined with the terrain silence rule, and validate the rest.
