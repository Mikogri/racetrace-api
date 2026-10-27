import unittest
import numpy as np
from subbagging import average_scores


class SubbaggingTests(unittest.TestCase):
    def test_raw_average_uses_every_fixed_component(self):
        values = np.array([[1., 2., 3.], [3., 4., 5.]])
        np.testing.assert_array_equal(average_scores(values, [0, 1], False), [2., 3., 4.])

    def test_midrank_uses_calibration_only_and_preserves_ties(self):
        values = np.array([[0., 0., 2., 100.], [10., 10., 20., -100.]])
        np.testing.assert_array_equal(average_scores(values, [0, 1, 2]), [1/3, 1/3, 5/6, .5])
        changed = values.copy()
        changed[:, 3] *= 100
        np.testing.assert_array_equal(average_scores(changed, [0, 1, 2])[:3],
                                      average_scores(values, [0, 1, 2])[:3])

    def test_invalid_inputs_rejected(self):
        for values, indices in [([[np.nan, 1]], [0]), ([[1, 2]], [0, 0]), ([[1, 2]], [2]), ([], [0])]:
            with self.assertRaises(ValueError):
                average_scores(values, indices)
