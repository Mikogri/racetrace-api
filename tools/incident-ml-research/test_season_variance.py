import pickle
import unittest
import numpy as np
from season_variance import risk_variance, fit_season_variance


class SeasonVarianceTests(unittest.TestCase):
    def test_exact_gradient_and_diagonal_curvature(self):
        x = np.array([-2., .4, 1.1, -1.5, .7, 2., -.2])
        y = np.array([1, 0, 0, 1, 1, 0, 0], bool)
        groups = np.array(['a']*3+['b']*4)
        for strength in [0., .5, 2.]:
            loss, gradient, hessian, surrogate, _ = risk_variance(x, y, groups, strength)
            self.assertTrue(np.isfinite(loss) and (surrogate > 0).all())
            for i in range(len(x)):
                plus, minus = x.copy(), x.copy()
                plus[i] += 1e-5
                minus[i] -= 1e-5
                lp, gp, _, _, _ = risk_variance(plus, y, groups, strength)
                lm, gm, _, _, _ = risk_variance(minus, y, groups, strength)
                self.assertAlmostEqual((lp-lm)/2e-5, gradient[i], places=7)
                self.assertAlmostEqual((gp[i]-gm[i])/2e-5, hessian[i], places=7)

    def test_zero_penalty_balances_observed_classes_within_each_domain(self):
        y = np.array([1, 0, 0, 1, 0], bool)
        out = risk_variance(np.zeros(5), y, ['a']*3+['b']*2, 0)
        self.assertAlmostEqual(out[0], 5*np.log(2))
        np.testing.assert_allclose(out[1], [-.625, .3125, .3125, -.625, .625])
        np.testing.assert_array_equal(out[2], out[3])
        np.testing.assert_allclose(list(out[4].values()), np.log(2))

    def test_constant_domain_renaming_and_single_domain_are_invariant(self):
        x, y = np.array([-.7, .5, 1.]), np.array([1, 0, 0], bool)
        zero = risk_variance(x, y, ['a']*3, 0)
        penalized = risk_variance(x, y, ['other']*3, 2)
        for a, b in zip(zero[:4], penalized[:4]):
            np.testing.assert_array_equal(a, b)

    def test_invalid_or_one_class_domains_are_rejected(self):
        for x, y, domains, strength in [([0, 0], [1, 0], ['a', 'b'], 1),
                ([np.nan, 0], [1, 0], ['a', 'a'], 1), ([0, 0], [1, 0], ['a', 'a'], -1)]:
            with self.assertRaises(ValueError):
                risk_variance(x, y, domains, strength)

    def test_native_model_reload_needs_no_domain_identifiers(self):
        x = np.c_[np.linspace(-2, 2, 300), np.tile([0., 1.], 150)]
        y = x[:, 0] > .5
        domains = np.tile(['a', 'b', 'c'], 100)
        model = fit_season_variance(x, y, domains, 7, .5, rounds=30)
        values = model.predict(x, raw_score=True, num_threads=2)
        self.assertGreater(np.median(values[y]), np.median(values[~y]))
        np.testing.assert_array_equal(values, pickle.loads(pickle.dumps(model)).predict(x, raw_score=True, num_threads=2))
