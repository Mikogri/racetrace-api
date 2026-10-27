import unittest
import numpy as np
from bounded_risk import observed_loss


class BoundedRiskTests(unittest.TestCase):
    def test_gradient_and_true_curvature_by_finite_differences(self):
        margins = np.array([-3., -.8, .4, 2., -1.2, .9])
        y = np.array([1, 1, 1, 0, 0, 0], bool)
        for q in [0., .3, .7]:
            for symmetric in [False, True]:
                loss, gradient, curvature, fisher = observed_loss(margins, y, q, symmetric)
                self.assertTrue(np.isfinite(loss) and np.all(fisher > 0))
                for i in range(len(margins)):
                    plus, minus = margins.copy(), margins.copy()
                    plus[i] += 1e-5
                    minus[i] -= 1e-5
                    lp, gp, _, _ = observed_loss(plus, y, q, symmetric)
                    lm, gm, _, _ = observed_loss(minus, y, q, symmetric)
                    self.assertAlmostEqual((lp-lm)/2e-5, gradient[i], places=7)
                    self.assertAlmostEqual((gp[i]-gm[i])/2e-5, curvature[i], places=7)

    def test_asymmetric_rule_keeps_positive_log_loss_and_downweights_hard_unknown(self):
        y = np.array([1, 1, 0, 0], bool)
        margins = np.array([-4., 4., -4., 4.])
        _, g0, _, _ = observed_loss(margins, y, 0, False)
        _, gq, _, _ = observed_loss(margins, y, .7, False)
        np.testing.assert_array_equal(g0[:2], gq[:2])
        self.assertLess(abs(gq[-1]), abs(g0[-1])/10)

    def test_zero_strength_is_balanced_logistic_and_extremes_are_finite(self):
        y = np.array([1, 0, 0, 0], bool)
        loss, g, h, fisher = observed_loss(np.zeros(4), y, 0, True)
        self.assertAlmostEqual(loss, 4*np.log(2))
        np.testing.assert_allclose(g, [-1., 1/3, 1/3, 1/3])
        np.testing.assert_array_equal(h, fisher)
        for q in [0., .3, .7]:
            outputs = observed_loss(np.array([-1000., 1000., -1000., 1000.]), y, q, True)
            self.assertTrue(all(np.isfinite(value).all() for value in outputs))

    def test_invalid_data(self):
        for x, y, q in [([1.], [True], .3), ([np.nan, 0], [True, False], .3), ([0, 0], [True, False], -1)]:
            with self.assertRaises(ValueError):
                observed_loss(x, y, q, False)
