import unittest
import numpy as np
try:
    from rolling_history import scoped_features
except ImportError:
    scoped_features = None


class RollingHistoryTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(callable(scoped_features), 'Causal rolling history is not implemented')
        self.meta = [dict(key=str(i)) for i in range(5)]
        self.records = {}
        for i, date, time in [(0, '2024-01-01', 50.), (1, '2024-02-01', 100.),
                              (2, '2024-03-01', 60.), (3, '2024-04-01', 70.),
                              (4, '2024-05-01', 900.)]:
            self.records[str(i)] = (np.full(5, time), np.full(5, 50.), np.full(5, 49.),
                                    'A', date, str(i), 'ME')

    def test_test_history_can_use_earlier_unlabelled_test_measurements_not_future(self):
        frozen, rolling = scoped_features(self.records, self.meta, [0], [1], [2, 3, 4])
        self.assertEqual(frozen[3, -1], 1)
        self.assertEqual(rolling[3, -1], 3)
        self.assertAlmostEqual(rolling[3, -3], np.log(1.2))
        self.records['4'] = (*self.records['4'][:3], 'A', '2024-05-01', '4', 'ME')
        changed = self.records.copy()
        changed['4'] = (np.full(5, 1e9), *self.records['4'][1:])
        _, after = scoped_features(changed, self.meta, [0], [1], [2, 3, 4])
        np.testing.assert_array_equal(rolling[3], after[3])

    def test_training_features_do_not_consume_even_earlier_held_measurements(self):
        fit_record = self.records['3']
        changed = self.records.copy()
        changed['0'], changed['2'] = fit_record, self.records['0']
        frozen, rolling = scoped_features(changed, self.meta, [0], [1], [2, 3, 4])
        np.testing.assert_array_equal(frozen[0], rolling[0])
        self.assertEqual(rolling[0, -1], 0)

    def test_calibration_history_cannot_consume_test_measurements(self):
        changed = self.records.copy()
        changed['1'], changed['2'] = self.records['3'], self.records['1']
        _, rolling = scoped_features(changed, self.meta, [0], [1], [2, 3, 4])
        self.assertEqual(rolling[1, -1], 1)
        self.assertAlmostEqual(rolling[1, -3], 0.)

    def test_same_day_measurements_are_not_ordered_by_ids(self):
        changed = self.records.copy()
        changed['2'] = (*self.records['2'][:4], '2024-04-01', '2', 'ME')
        _, rolling = scoped_features(changed, self.meta, [0], [], [1, 2, 3, 4])
        self.assertEqual(rolling[3, -1], 2)

    def test_forward_split_ignores_unassigned_measurements_even_if_they_are_earlier(self):
        changed = self.records.copy()
        changed['4'] = (*self.records['4'][:4], '2024-01-15', '4', 'ME')
        _, rolling = scoped_features(changed, self.meta, [0], [1], [2, 3])
        self.assertEqual(rolling[3, -1], 3)

    def test_relaxed_forward_scope_still_rejects_overlapping_roles(self):
        with self.assertRaises(ValueError):
            scoped_features(self.records, self.meta, [0], [1], [1, 2, 3])

    def test_placebo_recomputes_target_loss_using_another_riders_history(self):
        from rolling_history import placebo_features
        self.assertTrue(callable(placebo_features), 'Support-matched history placebo is not implemented')
        meta = [dict(key='1/A', category='ME'), dict(key='1/B', category='ME')]
        records = {row['key']: (np.full(5, 60.), np.full(5, 50.), np.full(5, 49.)) for row in meta}
        history = np.zeros((2, 13))
        history[0, -3:], history[1, -3:] = [0., .1, 1.], [np.log(1.4), .2, 1.]
        swapped, changed = placebo_features(history, records, meta)
        self.assertEqual(changed.tolist(), [True, True])
        self.assertAlmostEqual(swapped[0, 0], -10.)
        np.testing.assert_array_equal(swapped[:, -1], [1., 1.])
        self.assertEqual(history[0, -3], 0.)


if __name__ == '__main__':
    unittest.main()
