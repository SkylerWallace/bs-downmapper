/* Direction search for the supervised section-first exercise.
 * Uses the existing BSMap parity model, plus independent time-gap restrictions.
 * No reference map input. No claim of physical swing validation.
 */
const fs = require("fs"),
  path = require("path");
const runtime = path.resolve(
  __dirname,
  "../.tools/parity-runtime/node_modules/bsmap",
);
const { Parity } = require(runtime + "/script/extensions/parity/mod.js");
const angles = [90, 270, 180, 0, 135, 45, 225, 315];
const wrap = (a) => ((a + 540) % 360) - 180;
const dist = (a, b) =>
  a === 8 || b === 8 ? 45 : Math.abs(wrap(angles[a] - angles[b]));
const rotation = (a, b) =>
  a === 8 || b === 8 ? null : Math.abs(wrap(angles[b] - angles[a] - 180));
const allowance = (t) =>
  t <= 0.125 ? 0 : t <= 0.25 ? 45 : t <= 0.5 ? 90 : 135;
const protectedBeat = (b) => b < 16 || (b >= 294 && b < 324);
const clone = (s) => Object.assign(Object.create(Object.getPrototypeOf(s)), s);
function solve(input) {
  const ns = input.notes.map((n) => ({
    ...n,
    time: n.b,
    color: n.c,
    posX: n.x,
    posY: n.y,
    direction: n.d,
    angleOffset: n.a,
  }));
  const bombs = input.bombs.map((n) => ({
    ...n,
    time: n.b,
    color: -1,
    posX: n.x,
    posY: n.y,
  }));
  const edits = [],
    protectedIssues = [];
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
      const options = [];
      const ds = protectedBeat(src.b)
        ? [src.d]
        : src.b >= 32 && src.b < 36
          ? src.d < 2
            ? [0, 1]
            : [4, 5, 6, 7]
          : [0, 1, 4, 5, 6, 7];
      for (const entry of beam) {
        const context = bombs.filter(
          (b) =>
            b.time <= src.time + 0.00001 &&
            (!entry.prev || b.time > entry.prev.time),
        );
        for (const d of ds) {
          const n = {
            ...src,
            direction: d,
            d: d,
            angleOffset: protectedBeat(src.b) ? src.a : 0,
          };
          const dt = entry.prev ? ((n.time - entry.prev.time) * 60) / 140 : 999;
          const r = entry.prev ? rotation(entry.prev.direction, d) : null;
          if (dt <= 0.75 && r !== null && r > allowance(dt)) continue;
          const state = clone(entry.state),
            status = state.check([n], context);
          if (status !== "none" && !protectedBeat(src.b)) continue;
          state.next([n], context);
          // Preserve source motion vocabulary; no target diagonal percentage.
          const cost =
            entry.cost +
            (dist(src.d, d) / 45) * 1.5 +
            (dt > 0.75 && r !== null ? (r / 45) * 0.4 : 0) +
            (status === "none" ? 0 : 10);
          options.push({
            cost,
            state,
            prev: n,
            parent: entry,
            note: n,
            status,
          });
        }
      }
      if (!options.length)
        throw Error(
          "No jointly parity/time-admissible direction at " +
            src.b +
            " hand " +
            c,
        );
      const seen = new Set();
      beam = [];
      for (const e of options.sort((a, b) => a.cost - b.cost)) {
        const key = [e.state.state, e.state.rotation, e.note.d].join("/");
        if (!seen.has(key)) {
          seen.add(key);
          beam.push(e);
          if (beam.length === 32) break;
        }
      }
    }
    let best = beam[0];
    for (let i = seq.length - 1; i >= 0; i--) {
      const src = seq[i],
        n = best.note;
      if (src.d !== n.d)
        edits.push({ beat: src.b, hand: c, before: src.d, after: n.d });
      if (best.status !== "none")
        protectedIssues.push({
          beat: src.b,
          hand: c,
          status: best.status,
          reason:
            "protected source note; incoming state may differ from source and requires boundary review",
        });
      src.d = n.d;
      best = best.parent;
    }
  }
  return {
    notes: ns.map((n) => ({
      b: n.b,
      x: n.x,
      y: n.y,
      c: n.c,
      d: n.d,
      a: protectedBeat(n.b) ? n.a : 0,
    })),
    edits,
    protectedIssues,
  };
}
module.exports = { solve, rotation, allowance };
if (require.main === module) {
  const [input, output] = process.argv.slice(2);
  fs.writeFileSync(
    output,
    JSON.stringify(solve(JSON.parse(fs.readFileSync(input, "utf8"))), null, 2),
  );
}
