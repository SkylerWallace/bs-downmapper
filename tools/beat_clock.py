"""Piecewise conversion between authored beats and elapsed seconds."""

from bisect import bisect_right
from math import isfinite


class BeatClock:
    def __init__(self, bpm, audio=None, events=()):
        self.starts = [0]
        self.times = [0]
        self.rates = [60 / bpm]
        if audio is not None:
            regions = audio["bpmData"]
            frequency = audio["songFrequency"]
            if frequency <= 0 or not regions:
                raise ValueError("AudioData needs positive sample rate and tempo regions")
            self.starts = []
            self.times = []
            self.rates = []
            sample_tolerance = max(2, frequency * 0.01)
            for i, region in enumerate(regions):
                start, end = region.get("sb", 0), region["eb"]
                first, last = region.get("si", 0), region["ei"]
                previous = regions[i - 1] if i else None
                contiguous = not previous or (
                    abs(start - previous["eb"]) <= 0.0001 and abs(first - previous["ei"]) <= sample_tolerance
                )
                # Some shipped regions extend past the clip, followed by an inverted trim to its end.
                if (
                    previous
                    and i == len(regions) - 1
                    and contiguous
                    and end < start
                    and last < first
                    and last == audio.get("songSampleCount")
                    and self.starts[0] <= end <= previous["eb"]
                    and self.times[0] <= last / frequency <= previous["ei"] / frequency
                ):
                    break
                if (
                    not all(map(isfinite, (start, end, first, last)))
                    or end <= start
                    or last <= first
                    or not contiguous
                    or (previous and (start <= self.starts[-1] or first / frequency <= self.times[-1]))
                ):
                    raise ValueError("AudioData tempo regions must be positive and contiguous")
                if previous:
                    # Use the next authored start as the shared boundary when sample indexes disagree.
                    self.rates[-1] = (first / frequency - self.times[-1]) / (start - self.starts[-1])
                self.starts.append(start)
                self.times.append(first / frequency)
                self.rates.append((last - first) / frequency / (end - start))
        else:
            for event in sorted(events, key=lambda event: event["b"]):
                beat, tempo = event["b"], event["m"]
                if not isfinite(beat) or beat < 0 or not isfinite(tempo) or tempo <= 0:
                    raise ValueError("Invalid tempo event")
                time = self.seconds(beat)
                if beat == self.starts[-1]:
                    self.rates[-1] = 60 / tempo
                else:
                    self.starts.append(beat)
                    self.times.append(time)
                    self.rates.append(60 / tempo)

    def seconds(self, beat):
        i = max(0, bisect_right(self.starts, beat) - 1)
        return self.times[i] + (beat - self.starts[i]) * self.rates[i]

    def beat_at_seconds(self, seconds):
        i = max(0, bisect_right(self.times, seconds) - 1)
        return self.starts[i] + (seconds - self.times[i]) / self.rates[i]

    def gap(self, start, end):
        if len(self.rates) == 1:
            return (end - start) * self.rates[0]
        return self.seconds(end) - self.seconds(start)


def clock_for(level):
    return level.get("clock") or BeatClock(level["bpm"])
