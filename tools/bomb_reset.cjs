const axes = [[0, 1], [0, -1], [-1, 0], [1, 0], [-1, 1], [1, 1], [-1, -1], [1, -1]];

function resetPhase(previous, bombs) {
  let top = false;
  for (const note of previous ?? []) {
    const axis = axes[note.direction];
    if (!axis) continue;
    const angle = (note.angleOffset ?? 0) * Math.PI / 180;
    const scale = Math.hypot(...axis);
    const x = (axis[0] * Math.cos(angle) - axis[1] * Math.sin(angle)) / scale;
    const y = (axis[0] * Math.sin(angle) + axis[1] * Math.cos(angle)) / scale;
    for (const bomb of bombs) {
      if (bomb.posY !== 0 && bomb.posY !== 2) continue;
      const dx = bomb.posX - note.posX, dy = bomb.posY - note.posY;
      const along = dx * x + dy * y;
      const offset = Math.abs(dx * y - dy * x);
      const direct = along > .05 && along <= 1.5 && offset <= .45;
      const sideColumn = (bomb.posX === 0 || bomb.posX === 3) &&
        Math.abs(x) > .75 && dx * x > .05 && Math.abs(dx) <= 2 &&
        bombs.some(middle => Math.abs(middle.time - bomb.time) <= .0001 &&
          middle.posX === bomb.posX && middle.posY === 1 && Math.abs(middle.posY - note.posY) <= 1);
      if (!direct && !sideColumn) continue;
      if (bomb.posY === 0) return "forehand";
      top = true;
    }
  }
  return top ? "backhand" : null;
}

function applyReset(state, phase) {
  if (!phase) return;
  // BSMap stores the preceding phase, then flips it to check the next cut.
  state.state = phase === "forehand" ? "backhand" : "forehand";
  state.rotation = 0;
}

function parityBombContext(previous, bombs, next, gapSeconds) {
  if (!bombs.length) return { phase: null, bombs };
  const phase = gapSeconds <= 1.25
    ? resetPhase(previous, bombs.filter(bomb => bomb.time < next[0].time - .00001))
    : null;
  if (phase) return { phase, bombs: [] };
  const down = next.every(note => [1, 6, 7].includes(note.direction));
  return { phase, bombs: bombs.filter(bomb => bomb.posY !== 0 || down) };
}

module.exports = { resetPhase, applyReset, parityBombContext };
