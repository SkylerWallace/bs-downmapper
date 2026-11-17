const { run } = require("./check_parity.cjs");
const fs = require("fs"),
  os = require("os"),
  path = require("path"),
  assert = require("assert");
const dir = fs.mkdtempSync(path.join(os.tmpdir(), "bs-parity-tests-"));
const { resetPhase } = require("./bomb_reset.cjs");
const top = [{ time: 1, posX: 3, posY: 1 }, { time: 1, posX: 3, posY: 2 }];
const bottom = [{ time: 1, posX: 0, posY: 0 }, { time: 1, posX: 0, posY: 1 }];
const towardTop = { posX: 2, posY: 0, direction: 3 };
const towardBottom = { posX: 1, posY: 0, direction: 2 };
assert.equal(resetPhase([towardTop], top), "backhand");
assert.equal(resetPhase([towardBottom], bottom), "forehand");
assert.equal(resetPhase([towardTop], bottom), null);
assert.equal(resetPhase([towardTop, towardBottom], [...bottom, ...top]), "forehand");
assert.equal(resetPhase([towardTop, towardBottom], [...top, ...bottom]), "forehand");
assert.equal(resetPhase([{ posX: 0, posY: 0, direction: 0 }], [
  { time: 1, posX: 0, posY: 1 }, { time: 1, posX: 0, posY: 2 },
]), null);
const directionalAudit = [
  {
    previous: { x: 2, y: 0, d: 3 }, next: { x: 2, y: 0, d: 0 },
    bombs: top, phase: "backhand",
  },
  {
    previous: { x: 1, y: 0, d: 2 }, next: { x: 2, y: 0, d: 2 },
    bombs: bottom, phase: "forehand",
  },
];
for (const { previous, next, bombs, phase } of directionalAudit) {
  const chart = {
    version: "3.0.0",
    colorNotes: [previous, next].map((note, i) => ({ ...note, b: 2 * i, c: 1, a: 0 })),
    bombNotes: bombs.map(bomb => ({ b: bomb.time, x: bomb.posX, y: bomb.posY })),
  };
  const end = require("./check_parity.cjs").audit(chart, false, 100).trace.at(-1);
  assert.equal(end.bombReset, phase);
  assert.equal(end.swingState, phase);
  assert.equal(end.status, "none");
}
function map(name, events, bombs = []) {
  const filename = path.join(dir, name + ".dat");
  fs.writeFileSync(
    filename,
    JSON.stringify({
      version: "4.0.0",
      colorNotes: events.map((n, i) => ({ b: n.b, i })),
      colorNotesData: events.map((n) => ({ x: n.x ?? 1, c: 0, d: n.d })),
      bombNotes: bombs.map((b) => ({ b, i: 0 })),
      bombNotesData: bombs.length ? [{ x: 1, y: 0 }] : [],
      obstacles: [],
      obstaclesData: [],
      arcs: [],
      arcsData: [],
      chains: [],
      chainsData: [],
    }),
  );
  return filename;
}
const good = map("good", [
  { b: 0, d: 1 },
  { b: 0.5, d: 0 },
  { b: 1, d: 1 },
]);
assert.equal(run(good).errors, 0);
const bad = map("bad", [
  { b: 0, d: 1 },
  { b: 0.5, d: 0 },
  { b: 1, d: 1 },
  { b: 1.5, d: 1 },
]);
assert(run(bad).errors > 0);
run(bad, true);
assert.equal(run(bad).errors, 0);
const stack = map("stack", [
  { b: 0, d: 1, x: 0 },
  { b: 0, d: 1, x: 1 },
  { b: 0.5, d: 0 },
]);
assert.equal(run(stack).errors, 0);
assert.equal(run(stack).trace.length, 2);
const diagonal = map("diagonal", [
  { b: 0, d: 6 },
  { b: 0.5, d: 4 },
]);
run(diagonal, true, 140, true);
assert.equal(run(diagonal).errors, 0);
assert(
  JSON.parse(fs.readFileSync(diagonal)).colorNotes.every(
    (n) => JSON.parse(fs.readFileSync(diagonal)).colorNotesData[n.i].d < 4,
  ),
);
const repeat = [
  { b: 0, d: 1 },
  { b: 0.5, d: 0 },
  { b: 1, d: 1 },
  { b: 1.5, d: 1 },
  { b: 2, d: 0 },
];
assert(
  !run(map("same-beat-bomb", repeat, [1.5])).issues.some((i) => i.beat === 1.5),
);
assert(run(map("later-bomb", repeat, [2])).issues.some((i) => i.beat === 1.5));
console.log(
  "Passed: alternating flow, repeated-direction detection, repair recheck, simultaneous stack grouping, cardinal preference.",
);
// A relocated dot can change the parity of the later explicit cuts.
const { audit, polish } = require("./check_parity.cjs");
const bottomBombBeforeUpstroke = {
  version: "3.0.0",
  colorNotes: [0, 1].map(b => ({ b, c: 0, x: 1, y: 0, d: 0, a: 0 })),
  bombNotes: [{ b: .5, x: 1, y: 0 }],
};
assert.equal(audit(bottomBombBeforeUpstroke, false, 120).errors, 1);
assert.equal(audit(bottomBombBeforeUpstroke, false, 120, false, [{ hand: 0, beat: 1 }]).errors, 0);
const dotSequence = (directions, moved) => ({
  version: "3.0.0",
  colorNotes: directions.map((d, index) => ({
    b: [3, 5.5, 10][index], x: moved ? 1 : 3,
    y: moved ? 0 : [1, 0, 1][index], c: 0, d, a: index === 0 ? 45 : 0,
  })),
  bombNotes: [],
});
assert.equal(audit(dotSequence([8, 2, 0], false)).errors, 0);
assert(audit(dotSequence([8, 2, 0], true)).issues.some(
  (issue) => issue.beat === 10 && issue.hand === 0 && issue.status === "error",
));
assert.equal(audit(dotSequence([8, 0, 1], true)).issues.length, 0);
const restricted = () => ({
  version: "3.0.0",
  colorNotes: repeat.slice(0, 4).map(({ b, d }) => ({ b, d, c: 0, x: 0, y: 0, a: 0 })),
  bombNotes: [],
});
assert.equal(polish(restricted(), 140, [], { "0/1.5": null }).errors, 1);
const allowed = polish(restricted(), 140, [], { "0/1.5": [0] });
assert.equal(allowed.errors, 0);
assert.deepEqual(allowed.edits.map((edit) => edit.beat), [1.5]);
console.log("Passed moved-dot parity regression and corrected-direction control");
// Only remove the explicitly created fixture files/directory, without recursive deletion.
for (const filename of fs.readdirSync(dir))
  fs.unlinkSync(path.join(dir, filename));
fs.rmdirSync(dir);
