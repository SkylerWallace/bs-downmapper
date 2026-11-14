const assert = require("node:assert/strict");
const { solve, rotation, allowance } = require("./section_parity.cjs");
function note(b, c, d, x = c ? 2 : 1, y = 0) {
  return { b, c, d, x, y, a: 0 };
}
// Protected sparse source choreography must survive even when the checker
// dislikes an intentional reset. Do not 'repair' the user's good intro.
const intro = [note(4, 0, 6, 0, 1), note(8, 0, 7, 2), note(12, 0, 2, 0, 1)];
assert.deepEqual(solve({ notes: intro, bombs: [] }).notes, intro);
// Removing a subdivision can leave repeated down cuts. The search must repair
// parity and retain timing/hand; neither 'small rotation' nor deletion suffices.
const repeated = [
  note(32, 0, 1),
  note(32.5, 0, 1),
  note(33, 0, 6),
  note(33.5, 0, 6),
];
const fixed = solve({ notes: repeated, bombs: [] }).notes;
assert.deepEqual(
  fixed.map((n) => [n.b, n.c]),
  repeated.map((n) => [n.b, n.c]),
);
for (let i = 1; i < fixed.length; i++)
  assert.ok(
    rotation(fixed[i - 1].d, fixed[i].d) <= allowance((0.5 * 60) / 140),
  );
assert.ok(fixed.slice(0, 2).every((n) => n.d < 2));
assert.ok(fixed.slice(2).every((n) => n.d >= 4 && n.d <= 7));
assert.notEqual(fixed[0].d, fixed[1].d);
console.log(
  "Passed protected intro, parity-after-deletion, source stream families, timing/hand and time-gap checks",
);
