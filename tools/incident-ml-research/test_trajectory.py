import unittest
import numpy as np
from trajectory import run_shape, history_reference, historical_features, paired_rank_drop


class TrajectoryTests(unittest.TestCase):
    def test_isolated_loss_has_recovery_but_persistent_slowdown_does_not(self):
        isolated = run_shape([50, 50, 60, 50, 50], [50]*5, [49]*5)
        persistent = run_shape([50, 50, 60, 60, 60], [50]*5, [49]*5)
        self.assertAlmostEqual(isolated[2, 0], 10.)
        self.assertAlmostEqual(isolated[2, 4], 0.)
        self.assertGreater(isolated[2, 5], persistent[2, 5])

    def test_uniform_slowness_is_not_a_ten_second_jump(self):
        result = run_shape([60]*5, [50]*5, [49]*5)
        self.assertTrue(np.allclose(result[:, 0], 0.))

    def test_missing_timings_remain_missing_not_slow(self):
        result = run_shape([None, 50, None, None, None], [50]*5, [49]*5)
        self.assertEqual(result[0, -1], 1.)
        self.assertAlmostEqual(result[1, 0], 0.)
        self.assertTrue(np.isfinite(result).all())

    def test_history_excludes_same_round_other_category_and_future(self):
        records = [('2024-01-01', 'old', 'ME', -.1),
                   ('2024-02-01', 'now', 'ME', .8),
                   ('2024-01-01', 'junior', 'MJ', .9),
                   ('2024-03-01', 'future', 'ME', 1.)]
        ref = history_reference(records, '2024-02-02', 'now', 'ME')
        self.assertAlmostEqual(ref[0], -.1)
        self.assertEqual(ref[2], 1)

    def test_rank_drop_uses_the_same_riders_in_both_sectors(self):
        # The target is 2nd in both comparable sectors. Missing riders must not
        # create a fake position drop when the field shrinks.
        field = np.array([[50., 50.], [49., 49.], [51., np.nan], [52., np.nan]])
        self.assertEqual(paired_rank_drop(field, 0, 1), (0., 2))

    def test_history_measurements_must_come_from_fit_rows(self):
        records = {}
        for key, date, round_id, time in [('fit', '2024-01-01', 'a', 50.),
                                         ('held', '2024-02-01', 'b', 100.),
                                         ('test', '2024-03-01', 'c', 60.)]:
            records[key] = (np.full(5, time), np.full(5, 50.), np.full(5, 49.),
                            'rider', date, round_id, 'elite')
        features = historical_features(records, [{'key': k} for k in records], [0])
        self.assertEqual(features[2, -1], 1)
        self.assertAlmostEqual(features[2, -3], 0.)
        self.assertAlmostEqual(features[2, 0], 10.)

    def test_expected_time_is_never_faster_than_measured_best(self):
        result = run_shape([40, 40, 55, 40, 40], [50]*5, [48]*5)
        self.assertAlmostEqual(result[2, 0], 7.)


if __name__ == '__main__':
    unittest.main()
