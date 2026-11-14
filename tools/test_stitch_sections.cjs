const assert = require("node:assert/strict");
const { solve } = require("./stitch_sections.cjs");
function note(b, d, allowed = [d], extra = {}) {
  return {
    b,
    c: 1,
    x: 2,
    y: 0,
    d,
    a: 0,
    intent: {
      targetDirection: d,
      allowedDirections: allowed,
      weight: 1.5,
      familyWeight: 5,
      label: "fixture",
      anchor: allowed.length === 1,
      ...extra,
    },
  };
}
// A boundary lock must influence several earlier notes. The cheaper solution
// omits beat 29, reverses 30, and preserves the down entry at 32. A one-note
// repair at 32 would destroy the fixture's defining phrase.
const input = {
  notes: [
    note(28, 1),
    note(29, 1, [0, 1], { optional: true, omissionCost: 1 }),
    note(30, 1, [0, 1], { optional: true, omissionCost: 20 }),
    note(32, 1),
    note(32.5, 0),
  ],
  bombs: [],
};
const out = solve(input);
assert.deepEqual(
  out.notes.map((n) => [n.b, n.d]),
  [
    [28, 1],
    [30, 0],
    [32, 1],
    [32.5, 0],
  ],
);
assert.ok(
  out.edits.some(
    (e) => e.beat === 29 && e.operation === "omit_optional_pickup",
  ),
);
assert.equal(out.issues.length, 0);
// When both paths reach the anchor, a more expensive omission must lose.
const reverseCosts = structuredClone(input);
reverseCosts.notes[1].intent.omissionCost = 20;
reverseCosts.notes[2].intent.omissionCost = 1;
const other = solve(reverseCosts);
assert.ok(other.notes.some((n) => n.b === 29));
assert.ok(!other.notes.some((n) => n.b === 30));
assert.equal(other.notes.find((n) => n.b === 32).d, 1);
// Source lateral accents are allowed vocabulary, not globally prohibited.
const lateral = solve({
  notes: [note(20, 2, [2, 3]), note(22, 3, [2, 3])],
  bombs: [],
});
assert.ok(lateral.notes.every((n) => n.d === 2 || n.d === 3));
console.log(
  "Passed upstream stitch, anchor priority, omission-cost alternatives and lateral vocabulary regressions",
);
