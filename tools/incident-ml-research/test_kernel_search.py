import unittest
import numpy as np
from kernel_search import distance_contrast


class KernelSearchTests(unittest.TestCase):
    def test_pattern_closer_to_positive_than_control_receives_higher_score(self):
        result = distance_contrast(np.array([.1, 2.]), np.array([1., 1.]))
        self.assertGreater(result[0], result[1])

    def test_identical_vectors_do_not_create_infinity(self):
        self.assertTrue(np.isfinite(distance_contrast(np.array([0.]), np.array([0.]))).all())


if __name__ == '__main__':
    unittest.main()
