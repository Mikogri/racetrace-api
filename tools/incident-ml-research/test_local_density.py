import unittest
import numpy as np
from local_density import field_density, available_threshold


class LocalDensityTests(unittest.TestCase):
    def test_other_sessions_cannot_change_a_rider_reference(self):
        values = np.array([[0.], [.1], [.2], [.3], [10.], [1.], [2.], [3.]])
        groups = np.array(['a'] * 5 + ['b'] * 3)
        first = field_density(values, groups, np.ones(8, bool), .25)
        values[5:] *= 1000.
        second = field_density(values, groups, np.ones(8, bool), .25)
        np.testing.assert_allclose(first[:5], second[:5])
        self.assertGreater(first[4], first[1])

    def test_no_reference_or_no_measurement_remains_unknown(self):
        actual = field_density(np.zeros((3, 2)), np.array(['a', 'b', 'b']),
                               np.array([True, True, False]), .25)
        self.assertTrue(np.isnan(actual).all())

    def test_unavailable_rows_still_count_in_calibration_alarm_denominator(self):
        values = np.array([.1, .2, .3, np.nan])
        threshold = available_threshold(values, .25)
        self.assertAlmostEqual(threshold, .2)
        self.assertEqual(int(np.sum(values > threshold)), 1)


if __name__ == '__main__':
    unittest.main()
