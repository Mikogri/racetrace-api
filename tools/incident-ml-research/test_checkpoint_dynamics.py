import unittest
import numpy as np
from checkpoint_dynamics import checkpoint_features


class CheckpointTests(unittest.TestCase):
    def test_large_checkpoint_drop_is_measured_in_the_same_cohort(self):
        times = np.array([[10.,10.,20.,10.,10.], [11.,11.,11.,11.,11.],
                          [12.,12.,12.,12.,12.], [13.,13.,13.,13.,13.]])
        f = checkpoint_features(times, 4)
        self.assertEqual(f[0, 2, 0], 3.)
        self.assertAlmostEqual(f[0, 2, 2], 7.)

    def test_final_position_is_not_a_predictor(self):
        times = np.array([[10.,10.,10.,10.,50.], [11.,11.,11.,11.,11.]])
        f = checkpoint_features(times, 4)
        self.assertEqual(f[0, 4, 0], 0.)
        self.assertEqual(f[0, 4, 2], 0.)

    def test_missing_checkpoint_does_not_fabricate_later_cumulative_positions(self):
        times = np.array([[10.,np.nan,10.,10.,10.], [11.,11.,11.,11.,11.]])
        f = checkpoint_features(times, 4)
        self.assertEqual(f[0, 2, 5], 0.)

    def test_padding_after_a_four_sector_finish_has_no_final_position(self):
        times = np.array([[10.,10.,10.,10.,np.nan], [11.,11.,11.,11.,np.nan]])
        f = checkpoint_features(times, 3)
        self.assertTrue(np.all(f[:, 4] == 0.))


if __name__ == '__main__':
    unittest.main()
