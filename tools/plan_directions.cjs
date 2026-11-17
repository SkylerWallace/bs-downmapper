/* Map-independent direction planning with optional elapsed-time samples.
 * Optional reference swing states guide the search.
 */
const { Parity } = require(
  "../.tools/parity-runtime/node_modules/bsmap/script/extensions/parity/mod.js",
);
const { parityBombContext, applyReset } = require("./bomb_reset.cjs");
const angles = [90, 270, 180, 0, 135, 45, 225, 315];
const wrap = (a) => ((((a + 180) % 360) + 360) % 360) - 180;
const angle = (d, a = 0) => angles[d] + a;
const dist = (a, b) =>
  a.d === 8 || b.d === 8
    ? 45
    : Math.abs(wrap(angle(b.d, b.a) - angle(a.d, a.a)));
const rotation = (a, b) =>
  a.d === 8 || b.d === 8
    ? null
    : Math.abs(wrap(angle(b.d, b.a) - angle(a.d, a.a) - 180));
const allowance = (t) =>
  t <= 0.125 ? 0 : t <= 0.25 ? 45 : t <= 0.5 ? 90 : 135;
const family = (d) => (d < 2 ? 0 : d < 4 ? 1 : d < 8 ? 2 : 3);
const upward = (d) => d === 0 || d === 4 || d === 5;
const downward = (d) => d === 1 || d === 6 || d === 7;
const severity = { none: 0, warning: 1, error: 2 };
function solve(input) {
  if (!(input.bpm > 0)) throw Error("Explicit BPM required");
  const gap = (a, b) =>
    a.seconds !== undefined && b.seconds !== undefined
      ? b.seconds - a.seconds
      : ((b.b - a.b) * 60) / input.bpm;
  const notes = input.notes.map((n) => ({
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
    posX: n.x,
    posY: n.y,
    color: -1,
  }));
  const startBombs = bombs.filter(bomb => bomb.posY !== 0);
  const output = [],
    edits = [],
    baseline = [],
    search = [],
    recoveries = [],
    drummingRepeats = [];
  const recoverySeconds =
    input.controls?.recoveryResetEnabled === false
      ? Infinity
      : (input.controls?.recoveryResetSeconds ?? Infinity);
  const recoveryCost = input.controls?.recoveryResetCost ?? 6;
  const parityAdherence = input.drumming?.parityAdherence ?? 0.5;
  const doublePreference =
    input.beginnerDirections?.synchronizedDoublePreference ?? 0;
  const transitions = input.transitionPreferences ?? {};
  for (const c of [0, 1]) {
    const leftVertical = new Map(
      output
        .filter((n) => n.d < 2 && !n.intent && !n.streamEntry)
        .map((n) => [n.b, n.d]),
    );
    const hand = notes.filter((n) => n.c === c);
    const gs = [];
    for (const n of hand) {
      if (!gs.length || n.b - gs.at(-1)[0].b > 0.0001) gs.push([]);
      gs.at(-1).push(n);
    }
    const initial = new Parity(notes, startBombs, c, 90, 45, 90);
    let state = Object.assign(
        Object.create(Object.getPrototypeOf(initial)),
        initial,
      ),
      last = null;
    const authoredStatus = input.authoredParity && new Map(
      input.authoredParity.filter((finding) => finding.hand === c)
        .map((finding) => [finding.beat.toFixed(4), finding.status]),
    );
    const sourceStatus = [];
    for (const g of gs) {
      const ctx = g[0].parityReentry
        ? []
        : bombs.filter(
            (b) => b.time <= g[0].time + 0.00001 && (!last || b.time > last.at(-1).time + 0.00001),
          );
      if (g[0].parityReentry) state = new Parity(g, [], c, 90, 45, 90);
      const context = parityBombContext(last, ctx, g, last ? gap(last.at(-1), g[0]) : Infinity);
      applyReset(state, context.phase);
      const seedStatus = state.check(g, context.bombs);
      sourceStatus.push(authoredStatus ? (authoredStatus.get(g[0].b.toFixed(4)) ?? "none") : seedStatus);
      baseline.push({ hand: c, beat: g[0].b, status: sourceStatus.at(-1) });
      state.next(g, context.bombs);
      last = g;
    }
    let beam = [
      { state: initial, cost: 0, prev: null, parent: null, group: null },
    ];
    for (let i = 0; i < gs.length; i++) {
      const source = gs[i];
      const candidates = [];
      const locked = source.some((n) => n.streamEntry);
      const partnerDirection = leftVertical.get(source[0].b);
      const matchDouble =
        doublePreference > 0 &&
        partnerDirection !== undefined &&
        !locked &&
        !source.some((n) => n.intent);
      const explicit = source.find((n) => n.intent?.allowedDirections)?.intent
        .allowedDirections;
      const anchor = source.find((n) => n.linkedAnchor);
      const domain = (
        anchor
          ? [anchor.d]
          : explicit ||
            (locked
              ? [source[0].d]
              : source.every((n) => n.d === 8)
                ? [8]
                : [0, 1, 2, 3, 4, 5, 6, 7])
      ).filter((d) =>
        source.every(
          (n) =>
            n.linkedAnchor ||
            !n.geometryDirections ||
            n.geometryDirections.includes(d),
        ),
      );
      // A horizontal cut can be required by geometry even when the source used a different swing family.
      const geometryRequiresHorizontal = domain.every((d) => family(d) === 1);
      const ds = domain.filter((d) =>
        source.every(
          (n) =>
            n.linkedAnchor || !input.excludedDirections?.[n.id]?.includes(d),
        ),
      );
      for (const parent of beam) {
        const ctx = source[0].parityReentry
          ? []
          : bombs.filter(
              (b) =>
                b.time <= source[0].time + 0.00001 &&
                (!parent.prev || b.time > parent.prev.time + 0.00001),
            );
        for (const d of ds) {
          const outward = d === (c === 0 ? 2 : 3);
          const outwardPreference = outward
            ? Math.min(...source.map((n) => n.outwardPreference ?? 0))
            : 0;
          const flattened = outward && source.every((n) => n.flattenOutward);
          const linkedRepair = source.some((n) => n.linkedDirectionRepair);
          const horizontalRepair =
            input.allowHorizontalRepair && !locked && !explicit;
          if (
            !anchor &&
            source.every((n) => n.d !== 2 && n.d !== 3) &&
            [2, 3].includes(d) &&
            !outwardPreference &&
            !flattened &&
            !linkedRepair &&
            !geometryRequiresHorizontal &&
            !horizontalRepair
          )
            continue;
          const group = source.map((n) => {
            const direction = n.linkedAnchor ? n.d : d;
            const offset = direction === n.d ? n.a : 0;
            return {
              ...n,
              d: direction,
              direction,
              a: offset,
              angleOffset: offset,
            };
          });
          const dt = parent.prev ? gap(parent.prev, group[0]) : Infinity;
          if (
            parent.prev?.intent?.flexiblePhase &&
            source[0].intent?.flexiblePhase &&
            parent.prev.intent.pairId === source[0].intent.pairId &&
            parent.prev.d === d
          )
            continue;
          const r = parent.prev ? rotation(parent.prev, group[0]) : null;
          const sourcePrevious = i ? gs[i - 1][0] : null;
          const unchanged =
            source.every((n, j) => n.d === group[j].d && n.a === group[j].a) &&
            (!parent.prev ||
              (sourcePrevious &&
                parent.prev.id === sourcePrevious.id &&
                parent.prev.d === sourcePrevious.d &&
                parent.prev.a === sourcePrevious.a));
          const over = dt <= 0.75 && r !== null && r > allowance(dt);
          const downstrokePreference = Math.min(
            ...source.map((n) => n.downstrokePreference ?? 0),
          );
          const minimumDrumGap = Math.max(
            ...source.map(
              (n) =>
                n.minimumDrumGapSeconds ??
                input.drumming?.minimumGapSeconds ??
                Infinity,
            ),
          );
          const drumRepeat =
            parityAdherence < 1 &&
            downstrokePreference > 0 &&
            d === 1 &&
            parent.prev?.d === 1 &&
            !parent.prev.intent &&
            !parent.prev.streamEntry &&
            !ctx.length &&
            dt >= minimumDrumGap;
          const sourceReset =
            parent.prev?.d === d &&
            source.every((n) => n.intent?.resetFromBeat === parent.prev.b);
          const bombContext = parityBombContext(parent.group, ctx, group, dt);
          const parityBombs = sourceReset ? [] : bombContext.bombs;
          const sourceEntryRepeat =
            dt >= 0.55 &&
            !ctx.length &&
            sourceStatus[i] !== "none" &&
            downward(d) &&
            downward(parent.prev?.d) &&
            downward(sourcePrevious?.d) &&
            source.every((n) => n.intent?.entry && n.d === d);
          const carried = source[0].parityReentry || sourceReset
            ? new Parity(group, [], c, 90, 45, 90)
            : !parent.prev
              ? new Parity(group, startBombs, c, 90, 45, 90)
              : Object.assign(
                  Object.create(Object.getPrototypeOf(parent.state)),
                  parent.state,
                );
          applyReset(carried, bombContext.phase);
          const modes = [{ state: carried, recovery: false }];
          if (
            dt >= recoverySeconds &&
            parent.prev &&
            source.some((n) => n.recoveryCue) &&
            !ctx.length &&
            d !== 8 &&
            !source.some((n) => n.intent?.kind === "bomb_return_reset")
          ) {
            modes.push({
              state: new Parity(group, [], c, 90, 45, 90),
              recovery: true,
            });
          }
          for (const mode of modes) {
            if (
              over &&
              !unchanged &&
              !ctx.length &&
              !mode.recovery &&
              !sourceReset &&
              !anchor
            )
              continue;
            const state = mode.state;
            const status = state.check(group, parityBombs);
            // Source-context issues may be retained explicitly, never called safe.
            const provisionalWarning =
              input.allowProvisionalParityWarning &&
              (status === "warning" || (input.authoredParity && status === "error")) &&
              sourceStatus[i] === "none";
            if (
              severity[status] > severity[sourceStatus[i]] &&
              !drumRepeat &&
              !sourceReset &&
              !provisionalWarning
            )
              continue;
            state.next(group, parityBombs);
            const swingState = state.state;
            const sourceParityCost =
              status !== "error" &&
              source[0].sourceParityState &&
              source[0].sourceParityState !== swingState
                ? 3 * parityAdherence
                : 0;
            const directionCost = source.reduce((cost, n, j) => {
              if (n.linkedAnchor) return cost;
              const target = n.intent?.targetDirection ?? n.d;
              const diagonalCost = n.diagonalCost ?? 0;
              let familyCost = family(target) === family(d) ? 0 : 4;
              if (diagonalCost > 0 && family(target) === 2 && family(d) === 0)
                familyCost = 1;
              if (flattened) familyCost = 0.5;
              const horizontalLoss =
                family(d) !== 1 ? (n.horizontalRetention ?? 0) : 0;
              const reverseHorizontal =
                family(target) === 1 && family(d) === 1 && target !== d
                  ? (transitions.reverseHorizontal ?? 0)
                  : 0;
              const phaseLoss =
                n.intent?.phaseAnchor &&
                !(
                  (upward(target) && upward(d)) ||
                  (downward(target) && downward(d))
                )
                  ? 14
                  : 0;
              const sourceLoss = input.preferSourceArrows && d !== n.d ? 18 : 0;
              const parallelLoss =
                n.parallelDoubleDirection != null && d !== n.parallelDoubleDirection
                  ? target === n.parallelDoubleDirection ? 4 : 10
                  : 0;
              return (
                cost +
                (dist({ d: target, a: target === n.d ? n.a : 0 }, group[j]) /
                  45) *
                  2 +
                familyCost +
                horizontalLoss +
                reverseHorizontal +
                phaseLoss +
                sourceLoss +
                parallelLoss +
                (d === n.doubleClearanceDirection ? (n.doubleClearanceCost ?? 0) : 0) +
                (family(d) === 2 ? diagonalCost : 0)
              );
            }, 0);
            const issueCost =
              status === "none" || sourceReset
                ? 0
                : sourceEntryRepeat
                  ? 6
                  : drumRepeat
                    ? 3 * parityAdherence
                    : status === "error"
                      ? (transitions.parityErrorCost ?? 40)
                      : (transitions.parityWarningCost ?? 15);
            const repeatedUpCost =
              upward(d) && upward(parent.prev?.d)
                ? Math.min(...source.map((n) => n.repeatedUpPenalty ?? 0))
                : 0;
            const doubleCost =
              matchDouble && d !== partnerDirection ? doublePreference : 0;
            const beginnerCost =
              (d === 1
                ? 0
                : Math.max(0, downstrokePreference - outwardPreference)) +
              repeatedUpCost +
              doubleCost;
            let transitionCost = 0;
            if (
              dt <= 0.6 &&
              !locked &&
              !source.some((n) => n.intent) &&
              family(d) === 2
            ) {
              if (family(parent.prev?.d) === 0)
                transitionCost += transitions.verticalToDiagonal ?? 0;
              if (d === (c === 0 ? 5 : 4) && downward(parent.prev?.d))
                transitionCost += transitions.downToInwardUp ?? 0;
            }
            if (
              dt <= 0.25 &&
              !anchor &&
              family(parent.prev?.d) === 1 &&
              (c === 0 ? [5, 7] : [4, 6]).includes(d)
            )
              transitionCost += transitions.horizontalToInwardDiagonal ?? 0;
            candidates.push({
              state,
              cost:
                parent.cost +
                directionCost +
                sourceParityCost +
                beginnerCost +
                issueCost +
                transitionCost +
                (provisionalWarning ? (bombContext.phase ? 150 : 30) : 0) +
                (over && !mode.recovery && !sourceReset ? 8 : 0) +
                (mode.recovery ? recoveryCost : 0),
              prev: group.at(-1),
              parent,
              group,
              status,
              over,
              recovery: mode.recovery,
              drumRepeat,
            });
          }
        }
      }
      if (!candidates.length)
        throw Error(
          `No supported direction path at beat ${source[0].b} (${c === 0 ? "left" : "right"} hand); source or target needs manual annotation`,
        );
      const seen = new Set();
      beam = [];
      for (const e of candidates.sort((a, b) => a.cost - b.cost)) {
        const key = [e.state.state, e.state.rotation, e.prev.d, e.prev.a].join(
          "/",
        );
        if (!seen.has(key)) {
          seen.add(key);
          beam.push(e);
          if (beam.length === 64) break;
        }
      }
    }
    search.push({
      hand: c,
      alternativeCosts: beam.slice(0, 8).map((e) => e.cost),
    });
    let best = beam[0];
    for (let i = gs.length - 1; i >= 0; i--) {
      if (best.drumRepeat)
        drummingRepeats.push({
          hand: c,
          fromBeat: best.parent.prev.b,
          beat: best.group[0].b,
          gapSeconds: gap(best.parent.prev, best.group[0]),
        });
      if (best.recovery)
        recoveries.push({
          hand: c,
          fromBeat: best.parent.prev.b,
          beat: best.group[0].b,
          gapSeconds: gap(best.parent.prev, best.group[0]),
          reason:
            "crouch-associated recovery/regrip hypothesis after sufficient rest; requires physical review",
        });
      for (let j = 0; j < best.group.length; j++) {
        const n = best.group[j];
        output.push(n);
        if (n.d !== gs[i][j].d)
          edits.push({
            id: n.id,
            beat: n.b,
            hand: c,
            before: gs[i][j].d,
            after: n.d,
            reason:
              "weighted state/transition repair with source identity cost",
          });
      }
      best = best.parent;
    }
  }
  return {
    notes: output
      .sort((a, b) => a.b - b.b || a.c - b.c)
      .map((n) => ({
        b: n.b,
        x: n.x,
        y: n.y,
        c: n.c,
        d: n.d,
        a: n.a,
        id: n.id,
        streamEntry: n.streamEntry || false,
        intent: n.intent || {},
        ...(n.flattenOutward ? { flattenOutward: true } : {}),
        ...(n.linkedAnchor ? { linkedAnchor: true } : {}),
        ...(n.domainRepair ? { domainRepair: n.domainRepair } : {}),
        ...(n.retainOpeningDot ? { retainOpeningDot: true } : {}),
      })),
    edits,
    baseline,
    search,
    recoveries,
    drummingRepeats,
    bpm: input.bpm,
    limitations: [
      "source-state issues are review items, not approved patterns",
      "bounded direction search; geometry checked separately",
      "no timing deletion for parity convenience",
      "source-derived stream entry locks; no song-specific templates",
    ],
  };
}
function main([input, output]) {
  const fs = require("fs");
  fs.writeFileSync(
    output,
    JSON.stringify(solve(JSON.parse(fs.readFileSync(input, "utf8"))), null, 2),
  );
}
module.exports = { solve, main };
if (require.main === module) main(process.argv.slice(2));
