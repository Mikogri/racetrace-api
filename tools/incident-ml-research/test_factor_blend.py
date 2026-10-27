import unittest
import numpy as np
try:
    from factor_blend import combine
except ImportError:
    combine = None


class FactorBlendTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(callable(combine), 'Calibration-only combination is not implemented')
        self.cal = np.array([[0., 10.], [1., 20.], [2., 30.], [3., 40.]])

    def test_mean_and_consensus_use_calibration_cdfs_not_raw_score_units(self):
        values = np.array([[1.5, 25.], [4., 0.]])
        np.testing.assert_array_equal(combine(self.cal, values, 'mean', .25), [.5, .25])
        np.testing.assert_array_equal(combine(self.cal, values, 'min'), [.5, 0.])
        np.testing.assert_array_equal(combine(self.cal, values, 'max'), [.5, 1.])

    def test_other_test_rows_never_change_a_riders_combined_score(self):
        one = np.array([[1.5, 25.]])
        many = np.vstack([one, [[9999., 9999.], [-9999., -9999.]]])
        self.assertEqual(combine(self.cal, one, 'mean')[0], combine(self.cal, many, 'mean')[0])

    def test_unknown_inputs_are_not_silently_treated_as_zero_or_clean(self):
        with self.assertRaises(ValueError):
            combine(self.cal, np.array([[np.nan, 3.]]), 'mean')
        with self.assertRaises(ValueError):
            combine(np.empty((0, 2)), np.array([[1., 3.]]), 'mean')


if __name__ == '__main__':
    unittest.main()
