import unittest
import numpy as np
from objectives import grouped_rows, balanced_observation_weights


class ObjectiveTests(unittest.TestCase):
    def test_query_rows_are_contiguous_without_dropping_unlabelled_runs(self):
        order, sizes = grouped_rows(np.array([2024, 2021, 2024, 2022, 2021]))
        self.assertEqual(order.tolist(), [1, 4, 3, 0, 2])
        self.assertEqual(sizes.tolist(), [2, 1, 2])
        self.assertEqual(sorted(order.tolist()), list(range(5)))

    def test_observed_positive_and_unlabelled_masses_are_balanced(self):
        labels = np.array([True, False, False, False])
        weights = balanced_observation_weights(labels)
        self.assertAlmostEqual(weights[labels].sum(), weights[~labels].sum())
        self.assertAlmostEqual(weights.sum(), 4.)

    def test_single_observation_class_is_rejected(self):
        with self.assertRaises(ValueError):
            balanced_observation_weights(np.ones(3, dtype=bool))


if __name__ == '__main__':
    unittest.main()
