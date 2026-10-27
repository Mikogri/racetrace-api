import unittest
import numpy as np
from nested_selection import inner_splits, selection_objective


class NestedSelectionTests(unittest.TestCase):
    def test_inner_validation_stays_inside_fit_and_preserves_round_groups(self):
        meta = [dict(group=g) for g in ['a', 'a', 'b', 'b', 'c', 'c', 'd', 'd']]
        fit = np.arange(6)
        coverage = np.zeros(8, int)
        for training, held in inner_splits(fit, meta):
            self.assertTrue(set(training) <= set(fit))
            self.assertTrue(set(held) <= set(fit))
            self.assertFalse({meta[i]['group'] for i in training} & {meta[i]['group'] for i in held})
            coverage[held] += 1
        np.testing.assert_array_equal(coverage, [1, 1, 1, 1, 1, 1, 0, 0])

    def test_selection_weights_positive_counts_not_small_fold_percentages(self):
        self.assertAlmostEqual(selection_objective([([1, 1, 1], 1), ([0, 0, 0], 9)]), .1)
        with self.assertRaises(ValueError):
            selection_objective([([0, 0, 0], 0)])


if __name__ == '__main__':
    unittest.main()
