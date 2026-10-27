import unittest
import numpy as np
from nnpu_search import nonnegative_pu


class NonnegativePuTests(unittest.TestCase):
    def check_gradient(self, raw, prior):
        positive_sample = np.array([True, True, False, False, False])
        risk, gradient, curvature, negative = nonnegative_pu(raw, positive_sample, prior)
        for j in range(len(raw)):
            plus, minus = raw.copy(), raw.copy()
            plus[j] += 1e-5
            minus[j] -= 1e-5
            numeric = (nonnegative_pu(plus, positive_sample, prior)[0]-
                       nonnegative_pu(minus, positive_sample, prior)[0])/2e-5
            self.assertAlmostEqual(gradient[j], numeric, places=6)
        self.assertGreaterEqual(risk, 0.)
        self.assertTrue(np.all(curvature > 0.))
        return negative

    def test_gradient_with_active_unlabelled_risk(self):
        self.assertGreater(self.check_gradient(np.array([.1, -.2, .4, -.4, .8]), .1), 0.)

    def test_gradient_when_negative_risk_is_clipped(self):
        self.assertLess(self.check_gradient(np.array([10., 9., -10., -9., -8.]), .1), 0.)

    def test_prior_must_be_a_non_degenerate_fraction(self):
        with self.assertRaises(ValueError):
            nonnegative_pu(np.array([0., 0.]), np.array([True, False]), 0.)


if __name__ == '__main__':
    unittest.main()
