import unittest
import numpy as np
from stacking_search import meta_features


class StackingTests(unittest.TestCase):
    def test_extreme_base_scores_are_finite_and_order_is_preserved(self):
        values = np.array([[0., .2], [1., .8]])
        result = meta_features(values)
        self.assertTrue(np.isfinite(result).all())
        self.assertEqual(result.shape, values.shape)
        self.assertTrue(np.all(result[1] > result[0]))


if __name__ == '__main__':
    unittest.main()
