import unittest

import numpy as np

from audio_onsets import SAMPLE_RATE, mix_beat_weights


class MixAudioTests(unittest.TestCase):
    def test_silent_windows_score_zero_and_a_sharp_attack_stands_out(self):
        signal = np.zeros(2 * SAMPLE_RATE, dtype=np.float32)
        times = [0.25, 1.0, 1.75]
        np.testing.assert_array_equal(mix_beat_weights(np, signal, times), [0, 0, 0])
        signal[SAMPLE_RATE : SAMPLE_RATE + 32] = 1
        scores = mix_beat_weights(np, signal, times)
        self.assertEqual(scores[0], 0)
        self.assertGreater(scores[1], 0.5)
        self.assertEqual(scores[2], 0)
