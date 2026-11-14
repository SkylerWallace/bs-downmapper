"""Suggest repeat windows from local meter and phase without altering any chart timings."""

from bisect import bisect_left, bisect_right
from collections import defaultdict
import math

from timing_sections import MATCH_TOLERANCE, likely_offsets, short_anchor_offsets


BAR_PERIODS = (2, 3, 4, 6)
PHASE_STEPS = 24
ANALYSIS_BLOCK = 16


def local_downbeats(beats, counts):
    """Infer bar-start hypotheses from recurring accents in overlapping neighborhoods."""
    if not beats:
        return {}
    accents = [
        1 + .55 * min(2, count - 1) + .65 * min(2, beats[i] - beats[i - 1])
        if i else 1 + .55 * min(2, count - 1)
        for i, count in enumerate(counts)
    ]
    downbeats = defaultdict(set)
    first_block = math.floor(beats[0] / ANALYSIS_BLOCK) * ANALYSIS_BLOCK
    for start in range(first_block, math.floor(beats[-1]) + 1, ANALYSIS_BLOCK):
        a = bisect_left(beats, start - 8)
        b = bisect_left(beats, start + ANALYSIS_BLOCK + 8)
        if b - a < 8:
            continue
        mean = sum(accents[a:b]) / (b - a)
        for period in BAR_PERIODS:
            by_phase = defaultdict(dict)
            for beat, accent in zip(beats[a:b], accents[a:b]):
                step = round(beat * PHASE_STEPS)
                phase = step % (period * PHASE_STEPS)
                bar = (step - phase) // (period * PHASE_STEPS)
                by_phase[phase][bar] = max(accent, by_phase[phase].get(bar, 0))
            ranked = []
            for phase, bars in by_phase.items():
                if len(bars) < max(3, (beats[b - 1] - beats[a]) / period * .50):
                    continue
                strength = sum(bars.values()) / len(bars)
                if strength >= mean + .06:
                    ranked.append((strength, len(bars), phase))
            if not ranked:
                continue
            # Ambiguous phase hypotheses remain candidates. Alignment will decide.
            best = max(ranked)[0]
            for score, _, phase in sorted(ranked, reverse=True)[:2]:
                if score < best - .20:
                    continue
                origin = phase / PHASE_STEPS
                first = math.ceil((start - origin) / period)
                end = math.ceil((start + ANALYSIS_BLOCK - origin) / period)
                for bar in range(first, end):
                    beat = origin + bar * period
                    if beats[0] - period <= beat <= beats[-1]:
                        downbeats[beat].add(period)
    return dict(downbeats)


def repeat_windows(beats, downbeats):
    """Try meter windows only where chart rhythm has already voted for the translation."""
    starts = sorted(downbeats)
    offsets = list(dict.fromkeys([*likely_offsets(beats), *short_anchor_offsets(beats)]))
    for first in starts:
        for offset in offsets:
            expected = first + offset
            right = bisect_left(starts, expected - MATCH_TOLERANCE)
            end = bisect_right(starts, expected + MATCH_TOLERANCE, lo=right)
            for second in starts[right:end]:
                lengths = {
                    period * bars for period in downbeats[first] & downbeats[second] for bars in (2, 4, 8)
                }
                for length in sorted(lengths):
                    if length > second - first + MATCH_TOLERANCE or second + length > beats[-1] + 1.05:
                        continue
                    first_end = second if abs(second - first - length - 1) <= MATCH_TOLERANCE else first + length
                    yield (first, first_end), (second, second + length)


def local_windows(section, downbeats):
    start, end = section
    for beat, periods in downbeats.items():
        if not start <= beat < end:
            continue
        for length in {period * bars for period in periods for bars in (1, 2, 4, 8)}:
            if 3 <= length <= 32 and beat + length <= end + MATCH_TOLERANCE:
                yield beat, min(end, beat + length)
