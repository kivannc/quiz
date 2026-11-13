#!/usr/bin/env python3
"""Turns checked question candidates into questions in a shipped set.

Questions are written in a compact form, one JSON object per line:

    {"q": "<question>", "a": "<correct answer>", "d": ["<wrong>", "<wrong>", "<wrong>"]}

`blind` strips the answer and shuffles the options, so somebody who did not write the questions can
answer them cold. They reply with one verdict per line:

    {"n": <candidate number, from 1>, "pick": "<option text>", "flags": ["<reason>", ...]}

`assemble` keeps a candidate only if its verdict picked the author's answer and flagged nothing,
hands out the suits so each is the right one equally often, and appends to the set file. It writes
the whole file or nothing: too few survivors is an error, not a short set.

Standard library only.
"""

import argparse
import json
import os
import random
import sys
from collections import Counter

SUITS = ["hearts", "diamonds", "clubs", "spades"]
SET_SIZE_BEFORE = 40
MAX_OPTION = 30
MAX_QUESTION = 90


def normalise(text):
    """Case-folds the Turkish way (I/ı, İ/i) and collapses whitespace, as the shipped-set tests do."""
    folded = text.replace("I", "ı").replace("İ", "i").lower()
    return " ".join(folded.split())


def read_lines(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def dump_set(data):
    """The exact form the shipped files are in, so untouched questions stay byte-for-byte the same."""
    return json.dumps(data, ensure_ascii=False, indent=2) + "\n"


def shape_problem(c):
    """Why a candidate cannot become a question whatever its verdict says, or None."""
    q, a, d = c.get("q"), c.get("a"), c.get("d")
    if not isinstance(q, str) or not q.strip():
        return "no question text"
    if not isinstance(a, str) or not a.strip():
        return "no answer"
    if not isinstance(d, list) or len(d) != 3 or not all(isinstance(x, str) and x.strip() for x in d):
        return "needs exactly three distractors"
    if len({normalise(x) for x in [a, *d]}) != 4:
        return "two options read the same"
    if len(q) > MAX_QUESTION:
        return f"question is over {MAX_QUESTION} characters"
    if any(len(x) > MAX_OPTION for x in [a, *d]):
        return f"an option is over {MAX_OPTION} characters"
    return None


def blind(candidates_path, out_path):
    rng = random.Random(candidates_path)
    with open(out_path, "w", encoding="utf-8") as out:
        for n, c in enumerate(read_lines(candidates_path), start=1):
            if shape_problem(c):
                continue  # assemble reports it; there is nothing to answer
            options = [c["a"], *c["d"]]
            rng.shuffle(options)
            out.write(json.dumps({"n": n, "q": c["q"], "options": options}, ensure_ascii=False) + "\n")
    return 0


def next_id(questions):
    """Continues the prefix most questions in the file use; the older prefixes are left alone."""
    prefixes = Counter(q["id"].rsplit("-", 1)[0] for q in questions)
    prefix = prefixes.most_common(1)[0][0]
    last = max(int(q["id"].rsplit("-", 1)[1]) for q in questions if q["id"].startswith(prefix + "-"))
    return prefix, last + 1


def assemble(set_path, candidates_path, verdicts_path, take, rejects_path):
    with open(set_path, encoding="utf-8") as f:
        data = json.load(f)
    questions = data["questions"]
    if len(questions) != SET_SIZE_BEFORE:
        print(f"{set_path}: has {len(questions)} questions, expected {SET_SIZE_BEFORE}", file=sys.stderr)
        return 1
    if take % len(SUITS) != 0:
        print(f"--take must be a multiple of {len(SUITS)}", file=sys.stderr)
        return 1

    verdicts = {v["n"]: v for v in read_lines(verdicts_path)}
    # Every set next to this one counts: the same question must not turn up in two categories.
    seen = set()
    for name in sorted(os.listdir(os.path.dirname(os.path.abspath(set_path)))):
        if name.endswith(".json"):
            with open(os.path.join(os.path.dirname(os.path.abspath(set_path)), name), encoding="utf-8") as f:
                seen.update(normalise(q["text"]) for q in json.load(f)["questions"])
    kept, rejects = [], []
    for n, c in enumerate(read_lines(candidates_path), start=1):
        reason = shape_problem(c)
        if reason is None:
            v = verdicts.get(n)
            if v is None:
                reason = "not checked"
            elif v.get("flags"):
                reason = "flagged: " + ", ".join(v["flags"])
            elif normalise(str(v.get("pick", ""))) != normalise(c["a"]):
                reason = f"checker answered '{v.get('pick')}', author says '{c['a']}'"
            elif normalise(c["q"]) in seen:
                reason = "same text as another question"
        if reason is None:
            seen.add(normalise(c["q"]))
            kept.append(c)
        else:
            rejects.append(f"{n}\t{reason}\t{c.get('q')}")

    if rejects_path:
        with open(rejects_path, "w", encoding="utf-8") as f:
            f.write("".join(line + "\n" for line in rejects))

    if len(kept) < take:
        print(f"{set_path}: only {len(kept)} candidates passed, need {take}", file=sys.stderr)
        return 1

    rng = random.Random(data["id"])
    correct_suits = SUITS * (take // len(SUITS))
    rng.shuffle(correct_suits)
    prefix, number = next_id(questions)
    for c, correct in zip(kept[:take], correct_suits):
        wrong = list(c["d"])
        rng.shuffle(wrong)
        texts = {correct: c["a"]}
        texts.update(zip([s for s in SUITS if s != correct], wrong))
        questions.append({
            "id": f"{prefix}-{number:02d}",
            "text": c["q"],
            "options": [{"marker": s, "text": texts[s]} for s in SUITS],
            "correct": correct,
        })
        number += 1

    with open(set_path, "w", encoding="utf-8") as f:
        f.write(dump_set(data))
    print(f"{set_path}: {len(questions)} questions ({len(kept)} passed, {len(rejects)} rejected)")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    b = sub.add_parser("blind")
    b.add_argument("candidates")
    b.add_argument("out")
    a = sub.add_parser("assemble")
    a.add_argument("set")
    a.add_argument("candidates")
    a.add_argument("verdicts")
    a.add_argument("--take", type=int, default=60)
    a.add_argument("--rejects")
    args = parser.parse_args(argv)
    if args.command == "blind":
        return blind(args.candidates, args.out)
    return assemble(args.set, args.candidates, args.verdicts, args.take, args.rejects)


if __name__ == "__main__":
    sys.exit(main())
