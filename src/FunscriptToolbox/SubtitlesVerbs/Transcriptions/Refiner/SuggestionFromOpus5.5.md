# Suggestions from Opus 5.5: improving the timings after a transcription (Nov 2026)

Ideas to try after the Decision probes (`GeminiDecisionProbes.md`). Nothing here was tested: every number below is a hypothesis or an estimate, not a result.
Anything about the APIs that I am not sure of (prices, audio tokens per second, logprobs, TTS, context caching) is marked **(to verify)**.

---

## 1. What the probes say (my reading)

1. **Finding is solved, picking is not.** The right dip is in the +/-2 s window for 94-96% of the boundaries. With the current terrain (`MaxWhisperGap` 25 ms), the snap alone
   picks it for Start / End: A kanji 86 / 62%, B kanji 79 / 56%, A romaji 79 / 81% (simulation table of `GeminiDecisionProbes.md`).
2. **Decision as a judge of audio is a dead end.** In probe 12, the no-audio control (statistics only) was as good as the audio variants: the model reads, it barely listens.
   In probe 14-all, the margin rule found on 35 subtitles fell apart on the other 233. Decision is deterministic, and the order of the clips biases its answer.
3. **Decision's strong comparison signal was on the easy case only**: P(same) 0.79 for identical clips vs 0.31 for different ones (sanity probe).
   The subtle version (A/B vs the widest clip in probe 1, "does clip A lose part of the text vs REFERENCE" in probe 3) gave a weak signal. A comparison only helps if it can be made coarse enough to be in the easy regime.
4. **Gemini is strong at what it was trained for: writing the text of audio, with timestamps.** The snap baseline itself comes from Gemini timestamps (2.5 Pro / 3 Flash on the Google API).
   The romaji transcription (finer split) moved the End from 56% to 75% good without any judge (measured with the old `MaxWhisperGap` 300 ms; 62% vs 81% with 25 ms).
   Changing what the transcription produces did more than any yes/no question.
5. **"Sure enough to skip the validation" is a statistics question, not a threshold question.** A threshold chosen on 35 (or 300) subtitles can't guarantee an error rate on the next scene.
   It needs a measured error rate with a confidence bound (section 3).

So the ideas below:
- ask the models to **write** (transcribe / align) and let the code compare, instead of asking yes/no questions;
- get diversity from **independent sources** (other transcriptions, shifted windows, sampling), not from re-ordering the clips;
- **measure** the confidence (a bucket certified by sampling) instead of choosing a threshold.

**Why Flash is not "just a retry" of the Decision probes:** Flash (`generateContent`) is generative. It can write a time or a text instead of picking a label, and with a temperature above 0 it can be sampled N times.
Decision is deterministic: its only source of diversity was re-ordering the clips. With Flash, the spread of N samples is a direct measure of the uncertainty, and it needs no candidates in the request (no order bias).

---

## 2. Common rules for testing any idea

- Start from the realistic starting point: the **Init** snapped bounds, with the current terrain (`MaxWhisperGap` = 25 ms, the new default). All End numbers must be measured against that new baseline, not the 300 ms one.
- Use **all the subtitles**, parts A **and** B of HNVR-141, then one **other scene** before believing anything (probe 14-all: a hand-picked set flatters any picker).
- "Good" = from 300 ms outward to 100 ms inward (as in the probes).
- Main metric: **coverage at a fixed error rate**: the share of the boundaries that would be marked confident, with at most 3% bad among them.
- Secondary metric, for the shortlist idea: the **top-2 recall** (the right dip is one of the 2 options).
- A **kill criterion** is written for each idea: stop as soon as it is not met, without tuning.
- Write a `<part>.<idea>.srt` per test with the verdicts (as for the probes), with the start times of the examples.
- `TimingGroundTruthAnalyzer` already compares the refiner's output with the user's fixed file: it can be the base of the evaluation once an idea is in an engine.
- In an engine, any confidence signal is written as the `[TimingConfidence:x] reason` line that the `Conf.` column of the Timing editor already reads (that is also where 3.1 hooks in).

---

## 3. Two framings (they change what "success" means)

### 3.1 Certify a bucket by sampling, instead of choosing a threshold

Any confidence signal (agreement of 2 snaps, unanimous Decision, silence >= 1000 ms...) splits the boundaries into "confident" and "to validate".
The question "are the confident ones good enough to skip?" is answered by **validating a random sample of the confident bucket**, not all of it:

- With n boundaries sampled at random in the bucket and 0 bad, the 95% upper bound of the error rate is about **3 / n** ("rule of three"). With 1 bad, about 4.7 / n.
  To certify "<= 3% bad": ~100 sampled with 0 bad, or ~160 with 1 bad.
- The rule of the bucket is **fixed** (not re-tuned), so the samples **add up across scenes**: after a few scenes, the bucket is certified, and on a new scene
  only a small "canary" sample (e.g. 10 boundaries of the bucket) is checked. If any of them is bad, that scene's bucket is not trusted (the voice, the noise or the speaking style is different).
- The sample must be **random within the bucket**: the Timing editor would mix a few random "confident" boundaries into the session, without saying which ones (so the user judges them like the others).
- This also uses the idea of **ordering the validations** (worst first, from the "Not tried yet" list of `GeminiDecisionProbes.md`): the order and the certification work together: the order for the "to validate" bucket, the sampling for the "confident" one.

This answers "is it sure enough?" directly, for any of the signals below, and it doesn't need to be right the first time: a bucket that fails its certification is just not used.

### 3.2 A top-2 shortlist, picked with one key

If the AI can't give one sure answer, it may give **two options that contain the right one** ~95% of the time. The user then listens to A / B and presses 1 or 2,
instead of moving the edge by hand. This fits the planned Timing editor work (special modes to validate the different pieces).
The goal moves from "top-1 precision" (out of reach so far) to "top-2 recall" (maybe already within reach with the terrain alone: the best dip of the window is right 94-96% of the time).
See idea 2.

---

## 4. Ideas: free or almost free, on the data that already exists

### Idea 1. Agreement between independent snaps (no AI spend for the first test)

- **What**: two transcriptions of the same audio give two snapped Init bounds. When both snap to the **same dip** (within ~50 ms), the boundary is likely right. When they disagree, it is a candidate for validation, and the 2 bounds are the top-2 of idea 2.
- **Data that already exists**: part A, the kanji transcription and the romaji transcription (`...manual-input-preparer-romaji.srt`), both snapped with `TimingRefineEngineNone`. Both must be re-snapped with the current terrain (`MaxWhisperGap` 25 ms).
  The two are split differently (273 vs 311 lines): only the boundaries found in both (matched by time/text) are compared.
- **How it differs from the probes**: no AI judge at all. The "votes" are produced by the transcriber, the thing Gemini does well. Probe 12 / 14 show a judge does not add information; two transcriptions might.
- **Hypothesis**: Start: the "agree" bucket covers more than 50% of the Starts with less than 5% bad. End: less coverage, but the disagreements should catch most of the 1.1-1.5 s too-wide Ends.
- **Caveat**: both transcriptions come from Gemini, so their errors are correlated (both may drop the same leading interjection). A third source with different chunk boundaries (a Flash transcription where the audio chunks are shifted by half a chunk) would break part of that correlation. Its cost is one Flash transcription of the scene.
- **Cost**: 0 for the first test (the two transcriptions exist). Then one extra transcription per scene, in production: the romaji transcription was not adopted partly
  because of its token cost; the free test is what decides whether that cost is worth paying this time (as a vote, not as the main transcription).
- **Kill**: the "agree" bucket covers less than 40% of the Starts, or has more than 5% bad, on part A.

### Idea 2. Top-2 recall of the cheap candidates (no AI)

- **What**: for each boundary, measure whether the right dip is one of 2 options: the Init snap + **one** alternative from a cheap rule. Possible alternatives:
  - the other transcription's snap (idea 1);
  - End: the last dip before the first silence >= X ms after the last voiced piece (the "inward" option: most bad Ends are too wide by 1.1-1.5 s);
  - Start: the next dip outward when there is a sound in front closer than 300 ms.
- **Test**: part A + B, with the validated manual-input. Purely a computation on the terrain (as for `terrainSim.fsx` / `knobs.fsx`, if they are still around).
- **Kill**: top-2 recall under ~92% for Start or End: one key press would then often not be enough, so the UI mode would not save time.
- **If it holds**: the AI's job becomes "order the two" (a tie-breaker), and Decision's pairwise request between the top-2 (from the "Not tried yet" list of `GeminiDecisionProbes.md`) becomes the natural next step.

### Idea 3. A speaking-rate prior and a global alignment of the whole scene (no AI)

- **What**: Japanese has a fairly steady rate of morae per second for a given speaker and style. With the reading of each line (romaji syllables, or a kana reading), the
  **speech time** between a Start and a candidate End (sum of the Speech pieces of the terrain) divided by the number of morae gives a rate. A candidate End
  that makes the rate far too slow is probably too wide (the extra breath / next sound). Then, instead of choosing each boundary alone, choose **all of them together**
  with a dynamic programming pass over the lines (Viterbi): each line picks its Start and End among its dips, the End of a line must be before the Start of the next,
  and the score adds the terrain (silence after, kind of the piece) + the rate prior + the votes of idea 1.
- **How it differs**: it builds on "Fix the initial End snapping without AI" (the "Not tried yet" list of `GeminiDecisionProbes.md`).
  Probe 12's `desc_only` (no audio) fixed about half of the too-wide Ends from the statistics only: the information is in the terrain and the text,
  so a rule that uses it explicitly should do better than an AI reading numbers. The DP uses the neighbours the way the per-boundary engines can't.
- **Reading of the lines**: the romaji transcription already gives syllables; otherwise one cheap Flash request per scene ("give the kana reading of each line"), or a local tokenizer.
- **Test**: first only the rate: its distribution on the validated lines of A and B (per speaker if known), and how well it separates the too-wide Ends. Then the DP.
- **Kill**: the DP does not beat the Init snap on the End by >= 5 points (both parts) without adding cuts.

---

## 5. Ideas with Flash: let it write, and compare in the code

### Idea 4. Local forced alignment by Flash, with shifted windows (the main one)

- **What**: one request per line, a window of ~8-10 s around the line, with the **exact text** of the line and its neighbours. Flash returns the start and the end of the line
  (and of its first and last words). No candidates are shown. Each returned time is **snapped to the nearest dip** in the code.
  Repeat K times (K = 3 to 5) with the window **shifted** by a fraction of a second (0, +130, +260 ms...) and/or with a temperature above 0.
  Confident = all K land on the same dip **and** it is the Init dip. When they land on 2 dips, those are the top-2 (3.2).
- **Why it may work where probe 8 failed**: probe 8 asked Decision to **choose among clips** (order bias, "barely listens"). Here the model does what it does in the transcription
  (timestamps), with the text given, which is easier than transcribing. The terrain gives the precision and the model only has to say which dip. The spread of the K answers
  is a measured uncertainty, not a margin tuned on 35 subtitles.
- **Variant (crazy but cheap): slowed audio.** Slow the window down 2x (ffmpeg `atempo`, the pitch is kept) and divide the returned times by 2.
  If the audio is tokenized at a fixed number of tokens per second (32/s in the older Gemini docs, **to verify** for the current models), this doubles the time resolution of what the model hears.
- **Needs**: an engine that returns good timestamps on the audio as sent: the override file says only gemini-2.5-pro and gemini-3-flash **on the Google API** do;
  on Poe (or Gemini 3.0) the audio is shortened (silences removed) before sending, so the returned times would map back wrong.
- **Cost**: per line, K requests of ~8-10 s of audio + ~200 tokens of text. ~270 lines x 5 = ~1350 requests per part. Flash price and audio tokens per second: **to verify**.
  Use the batch mode (-50%) since nothing is interactive.
- **Test**: 30 lines of part A from the Init bounds, then all of A and B. Compare to idea 1 (agreement of 2 transcriptions), which costs nothing.
- **Kill**: the "all K agree" bucket is not better than idea 1's "agree" bucket (coverage at <= 3% bad), or the K answers almost never agree (the model's timestamps are noise at this scale).

### Idea 5. Edge transcription: "write what you hear" at the candidate, compare in the code

- **What**: a short clip that starts **at the candidate Start** (300 ms of stolen silence in front, as in the probes), ~1.5 s long, **no text given**. Flash transcribes it in kana
  ("write a partial syllable as …"). The code compares the first 2-3 morae of its answer with the first 2-3 morae of the line. Mirror clip for the End.
  An "outside" clip (1.2 s before the candidate Start) is transcribed too: it must **not** contain the first morae of the line.
- **How it differs from probes 10, 13, 14**: those asked Decision yes/no questions about the romaji ("is the first syllable '<yo>'?"), squeezed in 0.2-0.8.
  Here the model **writes**, and the code compares a short prefix only (2-3 morae, not the full syllable count the user found too complex).
- **Main risk**: the model "completes" a cut syllable because it knows the word (a language model fills in). The clip has no text, to limit this.
- **Test 0 (calibration, before anything else)**: take 50 validated Starts, cut the clip 150 ms **inside** the speech, and see whether Flash still writes the cut syllable.
- **Kill**: it writes the cut syllable more than ~50% of the time in test 0: it can't see cuts, stop there.

### Idea 6. Deletion test: "is this piece part of the line?"

- **What**: for the too-wide End (the main End error: a breath or the next sound appended), take the line from its Start to the candidate End, **remove** the last piece
  (between the inner dip and the candidate End, replaced by stolen silence), and ask Flash to transcribe it. If the transcription still has all the morae of the line,
  the removed piece was not speech of the line: the End can move inward. Same for a Start with a sound in front.
- **How it differs from the EndTail check**: EndTail asks "what kind of sound is the extra?" (a judgment, without the text). Here the question is answered by a transcription and a code comparison.
- **Risk**: same as idea 5 (Japanese endings are predictable: です, ね, よ... the model may "hear" them anyway). Control: also delete a real final mora of a validated line and see whether it notices.
- **Kill**: in the control, it still writes the deleted final mora more than ~30% of the time.

### Idea 7. A dropped-interjection veto

- **What**: probe 14-all's confident cuts were mostly lines where the transcription **dropped** a leading interjection (ねえ, あー, んー, n-n).
  Flash re-transcribes the padded audio (Start - 1.5 s .. End + 1 s) **verbatim, interjections included**. If its text has extra morae before (after) the line's text,
  the Start (End) is **vetoed**: never confident, whatever the other signals say.
- **Why**: it is not a picker, it is a guard for any confident bucket (section 3.1). It targets a known class of confident errors.
- **Note**: the user's rule for moans / breaths vs interjections must be in the prompt (the truth keeps the interjections; the "ignore moans" preamble made the AI skip 'n-n').
- **Test**: part A. Count how many of probe 14-all's confident-bad Starts would be vetoed (examples: #232 24:15.6, #226 22:58.7, #82 06:04.4, #205 18:04.4) and how many good ones would be lost.
- **Kill**: it vetoes as many good boundaries as bad ones.

### Idea 8. Ask the transcription for timing-friendly output

- **What**: change the transcription prompt (Flash or 2.5 Pro, the ones that give good timings): split at every pause > ~300 ms, and write the **non-speech events** with their times
  (`[breath]`, `[moan]`, `[laugh]`). The non-speech events become "no-snap zones" for the End: the snap does not extend an End over a `[breath]` the model marked.
- **Why**: the romaji transcription's finer split gave the biggest End gain so far (+19 points, 62% -> 81% with the current terrain), without any judge. The too-wide Ends come from the transcription's End landing in the next sound.
- **Needs**: the same as idea 4: an engine on the Google API that returns timings on the audio as sent (no audio shortening).
- **Cost**: one transcription of part A with the changed prompt, then `TimingRefineEngineNone`.
- **Kill**: the End does not gain >= 5 points over the current baseline (`MaxWhisperGap` 25 ms), or the Start loses more than 2 points.

---

## 6. Decision: only where it showed a signal

I would not spend more on Decision as a judge of audio (probes 12, 14-all). Where it could still help:

- **As one cheap extra vote** in the agreement of idea 1 / idea 4: probe 9's unanimous verification (~27% of the boundaries, 2-4% bad) is an independent signal, cheap, and it already exists.
- **As the tie-breaker between the top-2** ("pairwise requests between the two most-voted dips", from the "Not tried yet" list): 2 clips, both orders. Useful only if idea 2's top-2 recall holds.
- **As the order of the validations** (from the "Not tried yet" list, AUC ~0.70-0.75): it feeds section 3.1.
- **Same / different comparison**: only if it can be made coarse (the easy regime of the sanity probe): see the crazy ideas (TTS reference).

---

## 7. Neon: only for tasks that are different from what Argon was asked

Argon (zero-shot timing checks) was never good enough and was expensive: asking Neon the same thing is not worth it. Two different uses:

### Idea 9. Few-shot with the scene's own validated lines

- **What**: after the user validated the first ~20 lines of a scene in the Timing editor, Neon gets **these lines as examples** (audio window + the validated times + why when there is a note)
  before the lines to align. It learns **this** speaker, **this** noise and **the user's rules** (moans out, interjections in, ~300 ms of silence ok).
- **How it differs from the Argon tests**: if those tests were zero-shot (no validated examples in the request), the model could not know the user's rules. Many errors of the probes were about the rules, not about hearing.
- **Cost**: long requests. With context caching (**to verify** for Neon), the examples are paid once per scene. Only on the boundaries that are not already confident (the residue after ideas 1-4).
- **Test**: part A, lines 1-20 as examples, evaluated on the rest of A (and B with A's examples).
- **Kill**: no gain over the Init snap on the residue, or it costs more than the user's time it saves.

### Idea 10. Neon on the hard residue only, as a shortlist maker

- **What**: only on the ~15-20% of boundaries where all the cheap signals disagree: Neon gives a **top-2**, not one answer, for the one-key UI mode (3.2).
- **Kill**: its top-2 recall on the residue is not better than the cheap rule's top-2 (idea 2).

---

## 8. Crazy ideas

- **Tones at the dips**: in ONE clip, put a very short tone (or click) exactly at each dip, a different pitch per dip (low, mid, high...). Ask: "which tone is the last one before the first word?"
  A single clip, so no clip-order bias (rotate which pitch goes with which dip, against a label bias). It asks for the time order of a very salient event vs the speech,
  which may be easier for an audio model than "is this cut?". The tones sit in the dips (silence-ish), so they should hide little speech.
- **The spectrogram as an image**: send an image of the spectrogram of the window (with the dips as labeled vertical lines) together with the audio, to Flash or Neon.
  Probe 12 showed the model reads; an image shows the structure (voicing bands, fricatives, breaths) instead of a few numbers. Images are cheap in tokens.
- **A TTS reference, for Decision's same / different signal**: synthesize the first 2-3 morae of the line with a Gemini TTS model (**to verify**), and ask Decision "does clip A start with the same sounds as reference R?" for each candidate.
  Decision was strong only on the easy comparison (identical vs different clips); the subtle one (probes 1 and 3) was weak. The open question is whether
  "these morae vs other morae" with a synthetic voice is coarse enough to be in the easy regime, or whether the different voice makes everything "different".
- **Reversed audio for the End**: the Starts are judged much better than the Ends (78% vs 53% for the snap, and in every probe). Reverse the clip so the End becomes an onset:
  the question "is there sound before X?" does not need the speech to be understood. Probably useless for any question that needs the words.
- **A crowd of Flash samples**: 20 samples at temperature 1 of idea 4. The **shape** of the distribution of the snapped dips is the answer: one peak = confident, two peaks = top-2, flat = hard.
- **Neon as an agent with tools** (play a clip, list the dips, move a boundary, re-listen): it works like the user in the editor. Very expensive. Only to learn which questions are useful, not for production.

---

## 9. Outside the Gemini family (alignment and ranking, not transcription)

The text already exists (from Gemini). Finding **where** a known text is in the audio is "forced alignment", a different (and older) problem than transcription:

- **CTC forced alignment** of the kana reading with a Japanese / multilingual acoustic model (e.g. the MMS models or a Japanese wav2vec2; which one works on breathy / whispered speech is **to verify**).
  Local and free. It is exactly the tool for "where does this text start and end". As one more independent vote in idea 1.
- **Local Whisper word timestamps** (Purfview faster-whisper is already in the pipeline) as another vote.
- **A learned ranker on the terrain features**, trained on **all** the user's past validated manual-input files (every validated scene is ground truth).
  Features per dip: silence before / after, kind of the pieces around (Speech / Breath / Click), voicing, dB drop, distance to the transcription's time, the mora rate of idea 3, the votes of idea 1.
  A logistic regression or a small boosted tree (ML.NET in C#), checked "leave one scene out". Probe 12's `desc_only` result says the information is there.

---

## 10. Suggested order

| Step | Idea | AI cost | Why first |
|---|---|---|---|
| 1 | 1. Agreement of kanji vs romaji snaps (part A) | 0 | The data exists; the result decides if "votes from transcriptions" is a path. |
| 2 | 2. Top-2 recall of the cheap candidates | 0 | Decides if the one-key UI mode (3.2) is worth building. |
| 3 | 3.1 Certification by sampling | 0 (UI work) | Needed by every confident bucket; fits the planned Timing editor work. |
| 4 | 8. Timing-friendly transcription prompt | 1 transcription | The transcription already gave the biggest gain. |
| 5 | 3. Rate prior, then the DP | 0 | Uses what `desc_only` showed: the information is in the terrain + text. |
| 6 | 4. Flash forced alignment, shifted windows (30 lines, then all) | small (to verify) | The main AI idea; compare to step 1. |
| 7 | 5 / 6. Edge transcription / deletion test (the calibration test first) | small | Cheap to kill with the calibration. |
| 8 | 7. Interjection veto | small | Protects any confident bucket. |
| 9 | 9 / 10. Neon few-shot / shortlist on the residue | higher | Only if steps 1-8 leave a well-defined residue. |
