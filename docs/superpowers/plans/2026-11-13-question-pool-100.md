# Question Pool 40 → 100 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Grow each of the 11 shipped question sets from 40 to 100 questions without letting a wrong or arguable question through.

**Architecture:** Authors write candidates in a compact JSON Lines format; a separate agent answers them blind; a small Python assembler keeps the survivors, assigns suits and appends to the set files. Content rules that used to be hand-kept become xUnit tests on the shipped sets.

**Tech Stack:** .NET 10 / xUnit (existing), Python 3 standard library only (assembler), Claude subagents (authoring and blind check).

**Spec:** `docs/superpowers/specs/2026-11-13-question-pool-100-design.md`

## Global Constraints

- The 40 existing questions in every file stay byte-for-byte as they are. The files round-trip exactly through `json.dumps(obj, ensure_ascii=False, indent=2) + "\n"`; the assembler writes in that form and nothing else.
- Question rules: read aloud from the television, exactly one defensible answer, no synonym distractors, no wording open to argument, option ≤ 30 characters, question ≤ 90 characters.
- Options are always listed in the order `hearts`, `diamonds`, `clubs`, `spades`.
- Each suit is the correct one 25 times per finished file.
- New ids continue the file's dominant prefix: `ad-41`, `bd-31`, `cg-31`, `ed-31`, `gk-21`, `hv-31`, `it-31`, `mf-31`, `sp-31`, `th-31`, `ym-41`, zero-padded to two digits, three once past 99.
- Legacy prefixes and hedged wording in existing questions are deliberate; do not touch them.
- Candidate and verdict files live in the session scratchpad, never in the repository.
- No tag, no compose pin, no server command: release is the owner's move.
- Commits and docs in English; commit email is the GitHub noreply address.

## Review Focus

- A candidate whose text duplicates an existing question in *another* set → rejected by the shipped-set test (Task 1).
- A candidate with two identical options, or whose correct answer also appears among its distractors → assembler refuses it (Task 2).
- Fewer than 60 survivors for a set → assembler exits non-zero and writes nothing, rather than shipping a short or unbalanced file (Task 2).
- Running the assembler twice on the same set → second run refuses, because the file is no longer at 40 (Task 2).
- A blind check that agrees with the author for the wrong reason (both wrong) → not testable; mitigated by the checker being told to flag any fact it is not sure of (Task 3).

---

### Task 1: Guard rails on the shipped sets

**Files:**
- Modify: `tests/Quiz.Tests/JsonQuestionSetSourceTests.cs` (add four facts next to the existing shipped-set facts)

- [ ] **Step 1: Add the tests**

Each loads `new JsonQuestionSetSource(ShippedQuestions.Directory).All()` as the existing ones do.

- `No_shipped_question_text_appears_twice`: normalise text with `Trim()`, collapse runs of whitespace, `ToLower(new CultureInfo("tr-TR"))`; group across **all** sets; assert no group has more than one member, naming the ids in the failure message.
- `Each_shipped_set_spreads_the_correct_suit_evenly`: per set, count `Correct` per `Marker` over all four markers (a missing marker counts 0); assert `max - min <= 1`, naming the set.
- `Shipped_text_fits_the_television`: every option `Text.Length <= 30`, every question `Text.Length <= 90`, naming the question id.
- `No_shipped_question_repeats_an_option`: per question, the four option texts are distinct under the same normalisation.

- [ ] **Step 2: Run** `dotnet test` — expected: all pass against today's 440 questions. A failure here is a finding about existing content: report it, do not edit the question.

- [ ] **Step 3: Commit** `test: hold shipped question sets to the content rules`

### Task 2: The assembler

**Files:**
- Create: `tools/add_questions.py`
- Create: `tools/test_add_questions.py` (`python3 -m unittest tools/test_add_questions.py`)

**Interfaces:**
- Candidate line (author writes): `{"q": "<question>", "a": "<correct answer>", "d": ["<wrong>", "<wrong>", "<wrong>"]}`
- Blind line (assembler writes): `{"n": <1-based candidate number>, "q": "<question>", "options": ["…", "…", "…", "…"]}` — options shuffled, answer not marked.
- Verdict line (checker writes): `{"n": <number>, "pick": "<option text, verbatim>", "flags": ["<reason>", …]}` — empty `flags` means clean.
- `add_questions.py blind <candidates.jsonl> <blind.jsonl>`
- `add_questions.py assemble <set.json> <candidates.jsonl> <verdicts.jsonl> [--take 60] [--rejects <out.txt>]`

- [ ] **Step 1: Write the tests** (temporary directory, a 40-question fixture set with 10 correct per suit):
  - `assemble` with 60 clean candidates → file has 100 questions; the first 40 serialise identically to before; new ids continue the dominant prefix; each suit correct exactly 25 times; options in suit order; output equals `json.dumps(..., ensure_ascii=False, indent=2) + "\n"`.
  - a candidate passes only if its verdict exists, `pick == a`, and `flags` is empty; others are skipped and written to `--rejects` with the reason.
  - a candidate with duplicate options, `a` among `d`, not exactly three distractors, an option over 30 or a question over 90 characters → rejected with that reason.
  - a candidate whose normalised text matches an existing question in the set or an earlier candidate → rejected.
  - fewer than `--take` survivors → exit code 1, set file unchanged.
  - set file not at 40 questions → exit code 1, unchanged.
  - same inputs twice into fresh copies → identical output (seeded shuffle; seed is the set id).
  - `blind` output never contains the key `a` and has all four options.

- [ ] **Step 2: Run the tests** — expected: fail, module missing.

- [ ] **Step 3: Implement.** Suit assignment: a list of 15 of each suit, shuffled with `random.Random(set_id)`, one per kept candidate; distractors shuffled with the same generator into the remaining three suits.

- [ ] **Step 4: Run the tests** — expected: pass.

- [ ] **Step 5: Commit** `tools: assembler that turns checked candidates into set questions`

### Task 3: Write and check, per category (11 times, in parallel)

**Files:** scratchpad only — `<set-id>.candidates.jsonl`, `<set-id>.blind.jsonl`, `<set-id>.verdicts.jsonl`.

- [ ] **Step 1: Author.** One agent per set. It reads the set file, then writes 72 candidates. Brief: the Global Constraints question rules; spread across the set's sub-themes (the legacy prefixes show them); difficulty mixed, family-friendly, ages roughly 10 and up; nothing time-sensitive ("the current…", records that change); no "which is not" pile-ups; distractors of the same kind and length as the answer; nothing that repeats or paraphrases an existing question.
- [ ] **Step 2:** `add_questions.py blind` for each set.
- [ ] **Step 3: Blind check.** One fresh agent per set, on the stronger model, given only the blind file and the existing set. For each line: pick the answer, and flag `ambiguous` (two defensible answers), `synonym` (two options mean the same), `disputed` (the fact is contested or it is not sure of it), `dated` (answer can change), `overlap` (same fact as an existing or earlier question), `wording` (unclear when read aloud). It writes one verdict per line and nothing else.
- [ ] **Step 4:** If a set has fewer than 60 clean candidates, the author writes a top-up batch for that set and steps 2–3 repeat for the batch.

### Task 4: Assemble and verify

**Files:** Modify: `src/Quiz/questions/*.json` (all 11); `README.md` only if its description of pool size is now wrong.

- [ ] **Step 1:** `add_questions.py assemble … --take 60 --rejects <scratchpad>/<set-id>.rejects.txt` for each set.
- [ ] **Step 2:** `git diff --stat` and `git diff | grep '^-[^-]'` — expected: no removed lines except each file's old closing lines being re-emitted identically (i.e. none).
- [ ] **Step 3:** `dotnet test` — expected: green, including Task 1's four facts.
- [ ] **Step 4:** Read a random sample of 5 new questions per set as a last human-style pass; anything doubtful is reported, not silently kept.
- [ ] **Step 5: Commit** `questions: grow every set from 40 to 100`, then collect the reject lists into one summary for the owner.
