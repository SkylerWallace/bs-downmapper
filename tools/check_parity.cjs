/* Local adapter to BSMap 2.3.5, adapting Map Check's MIT parity/swing loop.
 * Upstream: KivalEvan/BeatSaber-MapCheck, 56d16718ceff4f2df8804321037ad6a54a415398.
 * See tools/licenses/map-check-LICENSE. Not the full Map Check application.
 */
const { loadDifficulty } = require(
  "../.tools/parity-runtime/node_modules/bsmap/script/beatmap/loader/difficulty.js",
);
const { TimeProcessor } = require(
  "../.tools/parity-runtime/node_modules/bsmap/script/beatmap/helpers/timeProcessor.js",
);
const { Parity } = require(
  "../.tools/parity-runtime/node_modules/bsmap/script/extensions/parity/mod.js",
);
const swing = require(
  "../.tools/parity-runtime/node_modules/bsmap/script/extensions/swing/mod.js",
);
const { parityBombContext, applyReset } = require("./bomb_reset.cjs");
function audit(
  raw,
  repair = false,
  bpm = 140,
  preferCardinal = false,
  reentries = [],
  checkpoints = null,
) {
  const map = loadDifficulty(raw);
  if (map.difficulty.arcs.length || map.difficulty.chains.length)
    throw Error("Adapter coverage excludes arcs/chains");
  const notes = map.difficulty.colorNotes,
    bombs = map.difficulty.bombNotes;
  const tp = new TimeProcessor(bpm, map.difficulty.bpmEvents || []);
  const states = [0, 1].map((c) => new Parity(notes, bombs.filter(bomb => bomb.posY !== 0), c, 90, 45, 90));
  const resetStarts = new Set(
    reentries.map((cue) => cue.hand + "/" + cue.beat),
  );
  let appliedRestedReentries = 0;
  const events = [...notes].sort((a, b) => a.time - b.time);
  const last = {},
    contexts = [[], []],
    issues = [],
    edits = [],
    trace = [],
    previousSwing = {};
  const angles = [90, 270, 180, 0, 135, 45, 225, 315];
  function flush(c) {
    const ctx = contexts[c];
    if (!ctx.length) return;
    const start = ctx[0].time,
      previous = previousSwing[c]?.notes.at(-1)?.time;
    const bctx = bombs.filter(
      (b) =>
        b.time <= start + 0.00001 &&
        (previous === undefined || b.time > previous + 0.00001),
    );
    if (resetStarts.has(c + "/" + ctx[0].time)) {
      states[c] = new Parity(ctx, [], c, 90, 45, 90);
      appliedRestedReentries++;
    }
    const state = states[c], incoming = { ...state };
    const elapsed = previous === undefined ? Infinity : tp.toRealTime(start) - tp.toRealTime(previous);
    const { phase: bombPhase, bombs: parityBombs } = parityBombContext(previousSwing[c]?.notes, bctx, ctx, elapsed);
    const context = resetStarts.has(c + "/" + start) ? [] : parityBombs;
    applyReset(state, bombPhase);
    const before = state.check(ctx, context),
      original = ctx.map((n) => n.direction);
    if (repair && (before !== "none" || preferCardinal)) {
      const candidates = [];
      const first = ctx[0],
        prev = previousSwing[c];
      const keepDiagonal =
        prev &&
        (first.time - prev.time >= 1 - 0.0002 ||
          (Math.abs(first.posX - prev.posX) >= 1 &&
            Math.abs(first.posY - prev.posY) >= 1));
      for (let d = 0; d < 8; d++) {
        ctx.forEach((n) => (n.direction = d));
        const status = state.check(ctx, context);
        const distance = original.reduce(
          (s, x) =>
            s +
            (x === 8
              ? 90
              : Math.min(
                  Math.abs(angles[x] - angles[d]),
                  360 - Math.abs(angles[x] - angles[d]),
                )),
          0,
        );
        if (status === "none")
          candidates.push({
            d,
            distance:
              distance +
              (preferCardinal && d >= 4 && !keepDiagonal ? 90 * ctx.length : 0),
          });
      }
      ctx.forEach((n, i) => (n.direction = original[i]));
      if (candidates.length) {
        candidates.sort((a, b) => a.distance - b.distance || a.d - b.d);
        ctx.forEach((n) => (n.direction = candidates[0].d));
        if (ctx.some((n, i) => n.direction !== original[i]))
          edits.push({
            beat: ctx[0].time,
            hand: c,
            before: original,
            after: ctx.map((n) => n.direction),
            reason: before === "none" ? "cardinal_preference" : before,
          });
      }
    }
    const status = state.check(ctx, context);
    checkpoints?.[c].push({ notes: ctx, bombs: bctx, state: { ...state }, incoming,
      reset: resetStarts.has(c + "/" + start), status });
    if (status !== "none")
      issues.push({
        beat: ctx[0].time,
        hand: c,
        status,
        directions: ctx.map((n) => n.direction),
      });
    const entry = {
      beat: ctx[0].time,
      hand: c,
      state: state.state,
      rotation: state.rotation,
      status,
      count: ctx.length,
    };
    if (bombPhase) entry.bombReset = bombPhase;
    state.next(ctx, context);
    if (status !== "error")
      entry.swingState = state.state;
    trace.push(entry);
    previousSwing[c] = {
      time: ctx[0].time,
      posX: ctx[0].posX,
      posY: ctx[0].posY,
      notes: ctx,
    };
  }
  for (const n of events) {
    const c = n.color;
    if (last[c] && swing.next(n, last[c], tp, contexts[c])) {
      flush(c);
      contexts[c] = [];
    }
    last[c] = n;
    contexts[c].push(n);
  }
  for (let c = 0; c < 2; c++) if (last[c]) flush(c);
  return {
    engine: "BSMap 2.3.5 / adapted Map Check swing-context loop",
    parameters: {
      bpm,
      warningThreshold: 90,
      errorThreshold: 45,
      allowedRotation: 90,
    },
    appliedRestedReentries,
    errors: issues.filter((x) => x.status === "error").length,
    warnings: issues.filter((x) => x.status === "warning").length,
    issues,
    edits,
    trace,
    limitations: [
      "No arcs/chains in adapter",
      "Not a collision/comfort certificate",
      "Not the full web app",
      "Direction/state model; dot/bomb interpretation and swing grouping may be ambiguous",
    ],
    repairedNotes: repair ? notes : undefined,
  };
}

function run(filename, repair = false, bpm = 140, preferCardinal = false, reentries = []) {
  const fs = require("fs");
  const text = fs.readFileSync(filename, "utf8").replace(/^\uFEFF/, "");
  const { repairedNotes: notes, ...report } = audit(JSON.parse(text), repair, bpm, preferCardinal, reentries);
  if (repair) {
    const raw = JSON.parse(text);
    if (!raw.version.startsWith("4.")) throw Error("Repair supports v4 only");
    if (raw.colorNotes.length !== notes.length)
      throw Error("Note order/count mismatch");
    raw.colorNotes.forEach((e, i) => {
      if (Math.abs((e.b || 0) - notes[i].time) > 1e-5)
        throw Error("Reader reordered notes");
      const a = { ...raw.colorNotesData[e.i || 0], d: notes[i].direction };
      e.i = raw.colorNotesData.length;
      raw.colorNotesData.push(a);
    });
    fs.writeFileSync(filename, JSON.stringify(raw, null, 2));
  }
  return report;
}
function main([filename, output, mode, bpm = "140", cueFile]) {
  const fs = require("fs");
  if (!(Number(bpm) > 0)) throw Error("Positive BPM required");
  const result = run(
    filename,
    mode === "repair" || mode === "simplify",
    Number(bpm),
    mode === "simplify",
    cueFile ? JSON.parse(fs.readFileSync(cueFile, "utf8")) : [],
  );
  fs.writeFileSync(output, JSON.stringify(result, null, 2));
  console.log(
    JSON.stringify({
      errors: result.errors,
      warnings: result.warnings,
      edits: result.edits.length,
    }),
  );
}
function polish(chart, bpm, reentries, domains) {
  const trialLimit = 420;
  const checkpoints = [[], []];
  const tp = new TimeProcessor(bpm, []);
  const key = (beat, hand) => hand + "/" + beat;
  const groups = new Map();
  for (const note of chart.colorNotes) {
    const id = key(note.b, note.c);
    if (!groups.has(id)) groups.set(id, []);
    groups.get(id).push(note);
  }
  const hands = [0, 1].map((hand) =>
    [...groups.values()].filter((group) => group[0].c === hand),
  );
  const distance = (a, b) => {
    const angles = [90, 270, 180, 0, 135, 45, 225, 315];
    if (a === 8 || b === 8) return 90;
    const delta = Math.abs(angles[a] - angles[b]);
    return Math.min(delta, 360 - delta);
  };
  audit(chart, false, bpm, false, reentries, checkpoints);
  const locations = checkpoints.map((hand) => new Map(hand.flatMap((entry, index) =>
    entry.notes.map((note) => [note.time, index]),
  )));
  let attempts = 0;
  const edits = new Map();
  const failed = new Set();
  for (let pass = 0; pass < 24 && attempts < trialLimit; pass++) {
    const issue = checkpoints.flatMap((hand, color) => hand
      .filter(({ status }) => status === "error")
      .map(({ notes }) => ({ hand: color, beat: notes[0].time })))
      .sort((a, b) => a.beat - b.beat || a.hand - b.hand)
      .find(({ beat, hand }) => !failed.has(key(beat, hand)) && domains[key(beat, hand)] !== null);
    if (!issue) break;
    const id = key(issue.beat, issue.hand);
    const hand = hands[issue.hand];
    const neighborhoods = hand.filter((group) =>
      group[0].b <= issue.beat && (issue.beat - group[0].b) * 60 / bpm <= 1.25,
    ).slice(-5).reverse();
    let best;
    function tryChanges(groups, directions) {
      if (attempts >= trialLimit) return;
      attempts++;
      const changes = new Map(groups.map(([note], i) => [note.b, directions[i]]));
      const trial = evaluate(issue.hand, changes);
      if (trial && (!best || trial.cleared > best.trial.cleared))
        best = { groups, directions, trial };
    }
    for (const group of neighborhoods) {
      const { b, c } = group[0];
      const directions = [...(domains[key(b, c)] ?? [])]
        .filter((direction) => group.some((note) => note.d !== direction))
        .sort((a, b) => distance(a, group[0].d) - distance(b, group[0].d));
      for (const direction of directions) tryChanges([group], [direction]);
    }
    if (!best) {
      const index = hand.findIndex((group) => group[0].b === issue.beat);
      for (let start = Math.max(0, index - 2); index >= 0 && start <= index; start++) {
        for (let length = 2; length <= 5; length++) {
          const window = hand.slice(start, start + length);
          if (start + length <= index || window.length !== length ||
              (window.at(-1)[0].b - window[0][0].b) * 60 / bpm > 1.8)
            continue;
          for (const phase of [0, 1]) {
            const directions = window.map((_, i) => (i + phase) % 2);
            if (window.every(([note], i) => domains[key(note.b, note.c)]?.includes(directions[i])))
              tryChanges(window, directions);
          }
        }
      }
    }
    if (!best) {
      failed.add(id);
      continue;
    }
    best.groups.forEach((group, i) => {
      const first = group[0];
      const editId = key(first.b, first.c);
      const before = edits.get(editId)?.before ?? group.map((note) => note.d);
      setDirection(group, best.directions[i]);
      edits.set(editId, { beat: first.b, hand: first.c, before, after: group.map((note) => note.d) });
    });
    checkpoints[issue.hand].splice(best.trial.start, best.trial.entries.length, ...best.trial.entries);
    failed.clear();
  }
  return { ...audit(chart, false, bpm, false, reentries), edits: [...edits.values()] };

  function evaluate(hand, changes) {
    const swings = checkpoints[hand];
    const indices = [...changes.keys()].map((beat) => locations[hand].get(beat));
    const first = Math.min(...indices), last = Math.max(...indices);
    // The initial state is inferred from the opening notes. Keep those fixed.
    if (!Number.isFinite(first) || first === 0) return null;
    const entries = [];
    let state = Object.assign(Object.create(Parity.prototype), swings[first].incoming);
    let previous = swings[first - 1].notes;
    let cleared = 0;
    for (let i = first; i <= Math.min(last + 8, swings.length); i++) {
      const original = swings[i];
      if (!original) return cleared ? { start: first, entries, cleared } : null;
      const notes = original.notes.map((note) => {
        const direction = changes.get(note.time);
        if (direction === undefined || direction === note.direction) return note;
        return Object.assign(Object.create(Object.getPrototypeOf(note)), note, { direction, angleOffset: 0 });
      });
      const context = [];
      let prior = previous.at(-1);
      for (const note of notes) {
        if (swing.next(note, prior, tp, context.length ? context : previous) !== !context.length)
          return null;
        context.push(note);
        prior = note;
      }
      const incoming = { ...state };
      const elapsed = tp.toRealTime(notes[0].time) - tp.toRealTime(previous.at(-1).time);
      const { phase, bombs } = parityBombContext(previous, original.bombs, notes, elapsed);
      applyReset(state, phase);
      if (i > last && (original.reset ||
          state.state === original.state.state && state.rotation === original.state.rotation))
        return cleared ? { start: first, entries, cleared } : null;
      if (original.reset) return null;
      const status = state.check(notes, bombs);
      const inherited = domains[key(notes[0].time, hand)] === null;
      if (inherited && status !== original.status ||
          status === "error" && original.status !== "error") return null;
      if (!inherited && original.status === "error" && status !== "error") cleared++;
      entries.push({ ...original, notes, status, state: { ...state }, incoming });
      state.next(notes, bombs);
      previous = notes;
    }
    return null;
  }

  function setDirection(group, direction) {
    for (const note of group) {
      if (note.d !== direction) note.a = 0;
      note.d = direction;
    }
  }
}

module.exports = { audit, run, polish };
if (require.main === module) main(process.argv.slice(2));
