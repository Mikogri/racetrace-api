import unittest
import numpy as np
from objectives import calibration_partition


class CalibrationTests(unittest.TestCase):
    def test_grouped_calibration_never_splits_a_round(self):
        meta = [{'group': str(j//2)} for j in range(32)]
        years = np.array([2021+j//8 for j in range(32)])
        labels = np.array([j % 3 == 0 for j in range(32)])
        fit, cal = calibration_partition(np.arange(32), years, meta, labels, 'grouped')
        self.assertFalse({meta[i]['group'] for i in fit} & {meta[i]['group'] for i in cal})
        self.assertEqual(set(fit) | set(cal), set(range(32)))
        self.assertGreater(len(set(years[fit])), 1)

    def test_latest_year_policy_retains_the_previous_partition(self):
        years = np.array([2021, 2021, 2022, 2022])
        fit, cal = calibration_partition(np.arange(4), years, [{'group': str(j)} for j in range(4)],
                                         np.array([True, False, True, False]), 'latest')
        self.assertEqual(fit.tolist(), [0, 1])
        self.assertEqual(cal.tolist(), [2, 3])


if __name__ == '__main__':
    unittest.main()
