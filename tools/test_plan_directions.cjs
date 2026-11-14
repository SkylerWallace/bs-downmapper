const assert = require("node:assert/strict"),
  { solve } = require("./plan_directions.cjs");
function pattern(shift, scale = 1) {
  return [1, 1, 0, 0, 1, 1, 0, 0].map((d, i) => ({
    b: shift + i * 0.25 * scale,
    c: 1 - (i % 2),
    x: i % 2 ? 1 : 2,
    y: 0,
    d,
    a: 0,
    id: i,
    streamEntry: i < 2,
  }));
}
assert.throws(() => solve({ notes: pattern(3), bombs: [] }), /BPM/);
const a = solve({ bpm: 101, notes: pattern(7.125), bombs: [] });
const b = solve({ bpm: 167, notes: pattern(71.375), bombs: [] });
assert.deepEqual(
  a.notes.map((n) => n.d),
  [1, 1, 0, 0, 1, 1, 0, 0],
);
assert.deepEqual(
  a.notes.map((n) => n.d),
  b.notes.map((n) => n.d),
);
// Doubling BPM and beat gaps preserves actual transition time.
const sameTime = solve({ bpm: 202, notes: pattern(23, 2), bombs: [] });
assert.deepEqual(
  a.notes.map((n) => n.d),
  sameTime.notes.map((n) => n.d),
);
console.log(
  "Passed explicit tempo, arbitrary locations, entry anchors and real-time scaling checks",
);
// A structured reset annotation must override a generic parity-repair desire.
const reset = [
  {
    b: 17,
    c: 1,
    x: 2,
    y: 0,
    d: 6,
    a: 0,
    id: 0,
    intent: { allowedDirections: [6], targetDirection: 6 },
  },
  {
    b: 17.75,
    c: 1,
    x: 1,
    y: 0,
    d: 6,
    a: 0,
    id: 1,
    intent: { allowedDirections: [6], targetDirection: 6 },
  },
  { b: 18.5, c: 1, x: 3, y: 0, d: 5, a: 0, id: 2 },
];
const protectedReset = solve({
  bpm: 101,
  notes: reset,
  bombs: [{ b: 17.5, x: 2, y: 0 }],
});
assert.deepEqual(
  protectedReset.notes.slice(0, 2).map((n) => n.d),
  [6, 6],
);
assert.deepEqual(
  protectedReset.notes.map((n) => [n.b, n.c]),
  reset.map((n) => [n.b, n.c]),
);
console.log("Passed structured reset preservation without rhythm deletion");
const recoveryNotes = [
  { b: 7, c: 0, x: 1, y: 0, d: 1, a: 0, id: 0 },
  { b: 9, c: 0, x: 1, y: 0, d: 1, a: 0, id: 1, recoveryCue: true },
];
const withCue = solve({
  bpm: 101,
  notes: recoveryNotes,
  bombs: [],
  controls: { recoveryResetSeconds: 0.7, recoveryResetCost: 6 },
});
assert.equal(withCue.recoveries.length, 1);
assert.deepEqual(
  withCue.notes.map((n) => n.d),
  [1, 1],
);
const noCue = solve({
  bpm: 101,
  notes: recoveryNotes.map((n) => ({ ...n, recoveryCue: false })),
  bombs: [],
  controls: { recoveryResetSeconds: 0.7, recoveryResetCost: 6 },
});
assert.equal(noCue.recoveries.length, 0);
assert.deepEqual(
  noCue.notes.map((n) => n.d),
  [1, 0],
);
console.log("Passed cued recovery branch and uncued negative control");
const drumNotes = [
  { b: 21, c: 0, x: 1, y: 0, d: 1, a: 0, id: 0, downstrokePreference: 9 },
  { b: 23, c: 0, x: 1, y: 0, d: 0, a: 0, id: 1, downstrokePreference: 10 },
];
const drumming = { minimumGapSeconds: 0.8 };
const spaced = solve({ bpm: 101, notes: drumNotes, bombs: [], drumming });
assert.deepEqual(
  spaced.notes.map((n) => n.d),
  [1, 1],
);
assert.equal(spaced.drummingRepeats.length, 1);
const tight = solve({
  bpm: 101,
  notes: [drumNotes[0], { ...drumNotes[1], b: 21.5 }],
  bombs: [],
  drumming,
});
assert.deepEqual(
  tight.notes.map((n) => n.d),
  [1, 0],
);
assert.equal(tight.drummingRepeats.length, 0);
const strict = solve({
  bpm: 101,
  notes: drumNotes,
  bombs: [],
  drumming: { ...drumming, parityAdherence: 1 },
});
assert.deepEqual(
  strict.notes.map((n) => n.d),
  [1, 0],
);
assert.equal(strict.drummingRepeats.length, 0);
const timeAware = drumNotes.map((n, i) => ({
  ...n,
  b: 21 + i,
  seconds: 12 + i,
}));
assert.equal(
  solve({ bpm: 101, notes: timeAware, bombs: [], drumming }).drummingRepeats
    .length,
  1,
);
assert.equal(
  solve({
    bpm: 101,
    notes: timeAware.map(({ seconds, ...n }) => n),
    bombs: [],
    drumming,
  }).drummingRepeats.length,
  0,
);
const angledAnchor = { ...drumNotes[0], d: 6, a: 30, linkedAnchor: true };
assert.equal(
  solve({ bpm: 101, notes: [angledAnchor], bombs: [] }).notes[0].a,
  30,
);
const flexiblePair = {
  kind: "paired_hand_call_response",
  pairId: "a",
  flexiblePhase: true,
  allowedDirections: [0, 1],
};
const pair = solve({
  bpm: 101,
  bombs: [],
  notes: [
    { ...drumNotes[0], downstrokePreference: 0, streamEntry: true },
    {
      ...drumNotes[1],
      b: 22,
      d: 1,
      downstrokePreference: 0,
      intent: { ...flexiblePair, targetDirection: 1 },
    },
    {
      ...drumNotes[1],
      id: 2,
      b: 22.5,
      d: 0,
      downstrokePreference: 0,
      intent: { ...flexiblePair, targetDirection: 0 },
    },
  ],
});
assert.deepEqual(
  pair.notes.map((n) => n.d),
  [1, 0, 1],
);
const freeCut = {
  b: 13,
  c: 0,
  x: 0,
  y: 1,
  d: 7,
  a: 0,
  id: 40,
  geometryDirections: [3, 7],
  linkedDirectionRepair: true,
};
const setup = {
  b: 12.5,
  c: 0,
  x: 0,
  y: 1,
  d: 4,
  a: 0,
  id: 39,
  streamEntry: true,
};
const linkedRepair = solve({
  bpm: 126,
  notes: [setup, freeCut],
  bombs: [],
  excludedDirections: { 40: [7] },
});
assert.equal(linkedRepair.notes[1].d, 3);
assert.throws(
  () =>
    solve({
      bpm: 126,
      notes: [setup, { ...freeCut, linkedDirectionRepair: false }],
      bombs: [],
      excludedDirections: { 40: [7] },
    }),
  /No supported direction path/,
);
const forcedHorizontal = {
  ...freeCut,
  d: 4,
  geometryDirections: [2, 3],
  linkedDirectionRepair: false,
};
const horizontalRepair = solve({
  bpm: 126,
  notes: [setup, forcedHorizontal],
  bombs: [],
});
assert.equal(horizontalRepair.notes[1].d, 3);
assert.equal(horizontalRepair.notes[1].b, forcedHorizontal.b);
console.log("Passed spaced drumming repeat and tight-gap negative control");
