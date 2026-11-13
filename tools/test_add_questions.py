"""Run with: python3 -m unittest tools/test_add_questions.py"""

import json
import os
import sys
import tempfile
import unittest
from collections import Counter

sys.path.insert(0, os.path.dirname(__file__))
import add_questions as aq  # noqa: E402


def fixture_set():
    questions = []
    for i in range(1, 41):
        prefix = "ol" if i <= 10 else "nw"  # ten on an older prefix, thirty on the dominant one
        number = i if i <= 10 else i - 10
        questions.append({
            "id": f"{prefix}-{number:02d}",
            "text": f"Eski soru {i}?",
            "options": [{"marker": s, "text": f"{s} {i}"} for s in aq.SUITS],
            "correct": aq.SUITS[i % 4],
        })
    return {"id": "fixture", "title": "Fixture", "language": "tr", "questions": questions}


def candidate(i):
    return {"q": f"Yeni soru {i}?", "a": f"doğru {i}", "d": [f"yanlış {i}a", f"yanlış {i}b", f"yanlış {i}c"]}


class AssembleTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)

    def path(self, name):
        return os.path.join(self.dir.name, name)

    def write(self, name, candidates, verdicts=None, data=None):
        """Writes a set, its candidates and verdicts under `name`; clean verdicts unless given."""
        with open(self.path(name + ".json"), "w", encoding="utf-8") as f:
            f.write(aq.dump_set(data or fixture_set()))
        with open(self.path(name + ".c"), "w", encoding="utf-8") as f:
            f.writelines(json.dumps(c, ensure_ascii=False) + "\n" for c in candidates)
        if verdicts is None:
            verdicts = [{"n": n, "pick": c.get("a"), "flags": []} for n, c in enumerate(candidates, 1)]
        with open(self.path(name + ".v"), "w", encoding="utf-8") as f:
            f.writelines(json.dumps(v, ensure_ascii=False) + "\n" for v in verdicts)

    def run_assemble(self, name, take=60):
        return aq.assemble(self.path(name + ".json"), self.path(name + ".c"), self.path(name + ".v"),
                           take, self.path(name + ".rejects"))

    def read(self, name):
        with open(self.path(name), encoding="utf-8") as f:
            return f.read()

    def test_sixty_clean_candidates_make_a_balanced_hundred(self):
        self.write("s", [candidate(i) for i in range(60)])
        before = fixture_set()["questions"]

        self.assertEqual(0, self.run_assemble("s"))

        raw = self.read("s.json")
        data = json.loads(raw)
        self.assertEqual(raw, aq.dump_set(data))
        self.assertEqual(100, len(data["questions"]))
        self.assertEqual(before, data["questions"][:40])
        self.assertEqual([f"nw-{n:02d}" for n in range(31, 91)], [q["id"] for q in data["questions"][40:]])
        self.assertEqual({s: 25 for s in aq.SUITS}, dict(Counter(q["correct"] for q in data["questions"])))
        for i, q in enumerate(data["questions"][40:]):
            self.assertEqual(aq.SUITS, [o["marker"] for o in q["options"]])
            by_suit = {o["marker"]: o["text"] for o in q["options"]}
            self.assertEqual(f"doğru {i}", by_suit[q["correct"]])
            self.assertEqual(4, len(set(by_suit.values())))

    def test_ids_go_to_three_digits_past_ninety_nine(self):
        data = fixture_set()
        for i, q in enumerate(data["questions"], 1):
            q["id"] = f"nw-{i:02d}"
        self.write("s", [candidate(i) for i in range(60)], data=data)

        self.assertEqual(0, self.run_assemble("s"))

        ids = [q["id"] for q in json.loads(self.read("s.json"))["questions"]]
        self.assertEqual(["nw-99", "nw-100"], ids[98:])

    def test_a_candidate_passes_only_with_a_matching_unflagged_verdict(self):
        candidates = [candidate(i) for i in range(64)]
        verdicts = [{"n": n, "pick": c["a"], "flags": []} for n, c in enumerate(candidates, 1)]
        verdicts[0]["pick"] = candidates[0]["d"][0]   # checker disagreed
        verdicts[1]["flags"] = ["ambiguous"]          # checker flagged
        del verdicts[2]                               # never checked
        self.write("s", candidates, verdicts)

        self.assertEqual(0, self.run_assemble("s"))

        texts = [q["text"] for q in json.loads(self.read("s.json"))["questions"][40:]]
        self.assertEqual([f"Yeni soru {i}?" for i in range(3, 63)], texts)
        rejects = self.read("s.rejects").splitlines()
        self.assertEqual(3, len(rejects))
        self.assertIn("checker answered", rejects[0])
        self.assertIn("flagged: ambiguous", rejects[1])
        self.assertIn("not checked", rejects[2])

    def test_malformed_candidates_are_rejected_with_the_reason(self):
        bad = [
            ({"q": "A?", "a": "x", "d": ["x", "y", "z"]}, "two options read the same"),
            ({"q": "B?", "a": "x", "d": ["y", "Y ", "z"]}, "two options read the same"),
            ({"q": "C?", "a": "x", "d": ["y", "z"]}, "exactly three"),
            ({"q": "D?", "a": "x" * 31, "d": ["y", "z", "w"]}, "option is over 30"),
            ({"q": "E" * 91, "a": "x", "d": ["y", "z", "w"]}, "question is over 90"),
            ({"q": "eski SORU 7?", "a": "x", "d": ["y", "z", "w"]}, "same text"),
            ({"q": "Yeni  soru 0?", "a": "x", "d": ["y", "z", "w"]}, "same text"),
        ]
        self.write("s", [candidate(i) for i in range(60)] + [c for c, _ in bad])

        self.assertEqual(0, self.run_assemble("s"))

        rejects = self.read("s.rejects").splitlines()
        self.assertEqual(len(bad), len(rejects))
        for line, (_, reason) in zip(rejects, bad):
            self.assertIn(reason, line)

    def test_a_question_already_in_a_neighbouring_set_is_rejected(self):
        other = fixture_set()
        other["id"] = "other"
        other["questions"][0]["text"] = "Komşu sette olan soru?"
        self.write("other", [], data=other)
        self.write("s", [candidate(i) for i in range(60)]
                   + [{"q": "komşu sette olan soru?", "a": "x", "d": ["y", "z", "w"]}])

        self.assertEqual(0, self.run_assemble("s"))

        self.assertIn("same text", self.read("s.rejects"))

    def test_too_few_survivors_fails_and_leaves_the_set_alone(self):
        self.write("s", [candidate(i) for i in range(59)])
        before = self.read("s.json")

        self.assertEqual(1, self.run_assemble("s"))

        self.assertEqual(before, self.read("s.json"))

    def test_a_set_that_is_not_at_forty_is_refused(self):
        self.write("s", [candidate(i) for i in range(60)])
        self.assertEqual(0, self.run_assemble("s"))
        grown = self.read("s.json")

        self.assertEqual(1, self.run_assemble("s"))

        self.assertEqual(grown, self.read("s.json"))

    def test_the_same_inputs_give_the_same_file(self):
        for name in ("one/s", "two/s"):  # apart, or the second would see the first's questions as taken
            os.mkdir(self.path(os.path.dirname(name)))
            self.write(name, [candidate(i) for i in range(60)])
            self.assertEqual(0, self.run_assemble(name))

        self.assertEqual(self.read("one/s.json"), self.read("two/s.json"))

    def test_blind_hides_the_answer_and_keeps_all_four_options(self):
        candidates = [candidate(0), {"q": "Bozuk?", "a": "x", "d": ["x", "y", "z"]}, candidate(2)]
        self.write("s", candidates)

        self.assertEqual(0, aq.blind(self.path("s.c"), self.path("s.blind")))

        lines = [json.loads(line) for line in self.read("s.blind").splitlines()]
        self.assertEqual([1, 3], [line["n"] for line in lines])  # numbering follows the candidate file
        for line, c in zip(lines, (candidates[0], candidates[2])):
            self.assertEqual({"n", "q", "options"}, set(line))
            self.assertEqual(sorted([c["a"], *c["d"]]), sorted(line["options"]))


if __name__ == "__main__":
    unittest.main()
