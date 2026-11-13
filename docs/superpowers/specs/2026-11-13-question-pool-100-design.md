# Question pool: 40 → 100 per category

## Intent

A round draws 10 questions at random from its category. With 40 in the pool, a family that plays
the same category twice sees about 2.5 of the 10 again. At 100 that falls to about 1. The pool
grows from 440 to 1,100 questions (660 new) at the lowest cost that still keeps a wrong question
off the television.

## Goals

- Every one of the 11 shipped sets holds exactly 100 questions.
- The 40 existing questions in each file are byte-for-byte untouched.
- New questions follow the house rules: read aloud from the television, short options, exactly one
  defensible answer, no synonym distractors, no wording open to argument.
- Suit balance stops being hand-kept: each suit is the correct one 25 times per file.

## Non-goals

- No new categories, no new question kinds.
- No cross-round repeat prevention (needs state that outlives the room; rejected earlier).
- No release: tag, compose pin and merge order stay the owner's move.

## Anti-goals

- A factually wrong or arguable question on screen. Dropping a doubtful question is always
  preferred to keeping it; a category that ends up short gets a top-up batch, not a relaxed bar.

## Approaches

1. **Author in-session, verify independently (chosen).** No API key, no runtime dependency, no
   recurring cost. The cost is session tokens and a short owner review of borderline questions.
2. **Generate at runtime with an LLM.** Recurring API cost, a key on the server, and questions reach
   the screen unreviewed. Rejected.
3. **Translate an open dataset (OpenTDB etc.).** English and US-centric; does not fit proverbs or
   Turkish literature, and cleaning the translation costs more than writing. Rejected.

## How

**Compact authoring format.** Authors do not write the shipped JSON (about 470 bytes a question).
They write one line per question: text, correct answer, three distractors. This is the main cost
saver, and it removes suits from the author's hands entirely.

**Pipeline, per category:**

1. *Write.* One agent per category reads the existing 40 and writes about 70 candidates in the
   compact format (a margin of 10 for rejects).
2. *Blind check.* A second agent that did not write them gets each question with its four options
   shuffled and the answer hidden. It must answer, and flag anything with two defensible answers,
   a synonym distractor, a disputed fact, or overlap with an existing question. A question passes
   only if the blind answer matches the author's and nothing is flagged.
3. *Assemble.* `tools/add_questions.py` takes the first 60 survivors, assigns suits so each is
   correct 15 more times (25 in total), shuffles distractors with a fixed seed, and appends to the
   set file in the existing formatting.

**Ids.** New questions continue the file's dominant prefix (`gk-21…`, `bd-31…`, `ym-41…`). The
legacy prefixes stay as they are.

**Guard rails, as tests on the shipped sets** (CI now runs `dotnet test` on every pull request):

- no question text appears twice, within a set or across sets (case- and whitespace-insensitive);
- within a set, the most- and least-used correct suit differ by at most 1;
- no option is longer than 30 characters and no question longer than 90 (today's maxima: 27, 68);
- no question has two options with the same text.

These read shipped content through the public loader; they add no seam to production types.

## Builder's calls

- Tolerance of 1 on suit balance rather than exact equality, so a later single-question fix does
  not have to come in fours.
- Cheaper model for writing, stronger model for the blind check: errors are caught at the check.
- The assembler stays in the repo so a later move to 200 reuses the same line.

## Done

- 11 files × 100 questions; `git diff` on each file shows additions only.
- `dotnet test` green, including the new guard rails.
- A short list of questions the check rejected and why, for the owner to skim.
