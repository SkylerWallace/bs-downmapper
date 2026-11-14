/* Weighted, anchor-constrained direction stitching. No reference gameplay input.
 * BSMap supplies a parity-state proxy; independent time-gap limits still apply.
 * Phrase anchors take precedence over cheap local edits near a boundary.
 */
const fs = require("fs"),
  path = require("path");
const { Parity } = require(
  path.resolve(
    __dirname,
    "../.tools/parity-runtime/node_modules/bsmap/script/extensions/parity/mod.js",
  ),
);
const { rotation, allowance } = require("./section_parity.cjs");
const angles = [90, 270, 180, 0, 135, 45, 225, 315];
const wrap = (a) => ((a + 540) % 360) - 180;
const distance = (a, b) =>
  a === 8 || b === 8 ? 45 : Math.abs(wrap(angles[a] - angles[b]));
const family = (d) =>
  d < 2 ? "vertical" : d < 4 ? "horizontal" : d < 8 ? "diagonal" : "dot";
function solve(input) {
  const ns = input.notes.map((n) => ({
    ...n,
    time: n.b,
    color: n.c,
    posX: n.x,
    posY: n.y,
    direction: n.d,
    angleOffset: n.a || 0,
  }));
  const bombs = input.bombs.map((n) => ({
    ...n,
    time: n.b,
    color: -1,
    posX: n.x,
    posY: n.y,
  }));
  const edits = [],
    issues = [],
    searchLog = [],
    selected = [];
  for (let c = 0; c < 2; c++) {
    const seq = ns.filter((n) => n.c === c);
    let beam = [
      {
        cost: 0,
        state: new Parity(ns, bombs, c, 90, 45, 90),
        prev: null,
        parent: null,
        note: null,
      },
    ];
    for (const src of seq) {
      const candidates = [];
      for (const parent of beam) {
        if (src.intent.optional)
          candidates.push({
            cost: parent.cost + src.intent.omissionCost,
            state: parent.state,
            prev: parent.prev,
            parent,
            note: null,
            skipped: src,
            status: "none",
            parts: { omission: src.intent.omissionCost },
          });
        const context = bombs.filter(
          (b) =>
            b.time <= src.time + 0.00001 &&
            (!parent.prev || b.time > parent.prev.time + 0.00001),
        );
        for (const d of src.intent.allowedDirections) {
          const n = { ...src, d, direction: d };
          const dt = parent.prev
            ? ((src.time - parent.prev.time) * 60) / 140
            : 999;
          const r = parent.prev ? rotation(parent.prev.d, d) : null;
          if (dt <= 0.75 && r !== null && r > allowance(dt)) continue;
          const state = Object.assign(
            Object.create(Object.getPrototypeOf(parent.state)),
            parent.state,
          );
          const status = state.check([n], context);
          if (status !== "none" && !src.intent.allowInheritedIssue) continue;
          state.next([n], context);
          const goal = src.intent;
          const angle = distance(goal.targetDirection, d) / 45;
          const changedFamily = family(goal.targetDirection) !== family(d);
          const unnecessaryDiagonal =
            d >= 4 && d <= 7 && goal.targetDirection < 4;
          const parts = {
            angle: angle * goal.weight,
            family: changedFamily * goal.familyWeight,
            unnecessaryDiagonal: unnecessaryDiagonal * 3,
            transition: dt <= 0.75 && r !== null ? (r / 180) * 0.15 : 0,
            inheritedIssue: status === "none" ? 0 : 30,
          };
          const step = Object.values(parts).reduce((a, b) => a + b, 0);
          candidates.push({
            cost: parent.cost + step,
            state,
            prev: n,
            parent,
            note: n,
            status,
            parts,
          });
        }
      }
      if (!candidates.length)
        throw Error(
          "No stitch satisfies intent/parity/time at " +
            src.b +
            " hand " +
            c +
            " " +
            src.intent.label,
        );
      const seen = new Set();
      beam = [];
      for (const e of candidates.sort((a, b) => a.cost - b.cost)) {
        const key = [
          e.state.state,
          e.state.rotation,
          e.prev?.d,
          e.prev?.b,
        ].join("/");
        if (!seen.has(key)) {
          seen.add(key);
          beam.push(e);
          if (beam.length >= 128) break;
        }
      }
      if (src.intent.anchor)
        searchLog.push({
          hand: c,
          beat: src.b,
          label: src.intent.label,
          admissibleStates: beam.length,
          lowestCumulativeCost: beam[0].cost,
          note: "costs are internal preferences, not measured physical difficulty",
        });
    }
    searchLog.push({
      hand: c,
      finalAlternativeCosts: beam.slice(0, 8).map((e) => e.cost),
    });
    let best = beam[0];
    for (let i = seq.length - 1; i >= 0; i--) {
      const src = seq[i],
        n = best.note;
      if (!n) {
        edits.push({
          beat: src.b,
          hand: c,
          operation: "omit_optional_pickup",
          label: src.intent.label,
          stepCosts: best.parts,
        });
        best = best.parent;
        continue;
      }
      if (src.d !== n.d)
        edits.push({
          beat: src.b,
          hand: c,
          sourceDirection: src.d,
          targetDirection: src.intent.targetDirection,
          chosenDirection: n.d,
          label: src.intent.label,
          stepCosts: best.parts,
        });
      if (best.status !== "none")
        issues.push({ beat: src.b, hand: c, status: best.status });
      src.d = n.d;
      selected.push(src);
      best = best.parent;
    }
  }
  return {
    notes: selected
      .sort((a, b) => a.b - b.b || a.c - b.c)
      .map((n) => ({
        b: n.b,
        x: n.x,
        y: n.y,
        c: n.c,
        d: n.d,
        a: n.a || 0,
        intent: n.intent,
      })),
    edits,
    issues,
    searchLog,
    limitations: [
      "beam search over directions and parity state, not joint human motion",
      "rhythm/position templates are chosen outside this search",
      "future anchors can change several earlier notes; no automatic reset at section boundaries",
      "weights are project hypotheses; no audio or physical validation",
    ],
  };
}
module.exports = { solve };
if (require.main === module) {
  const [input, output] = process.argv.slice(2);
  fs.writeFileSync(
    output,
    JSON.stringify(solve(JSON.parse(fs.readFileSync(input, "utf8"))), null, 2),
  );
}
