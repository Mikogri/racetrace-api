import unittest
import numpy as np
from scipy.optimize._numdiff import approx_derivative
from scipy.integrate import quad
from scipy.stats import norm
from sector_sequence import emissions, posterior, fit_sequence, sequence_scores, observations, anchor_mask, log_prior, PATHS


class SequenceTests(unittest.TestCase):
    def test_path_prior_and_independent_permutation_invariance(self):
        self.assertAlmostEqual(float(np.exp(log_prior([.1, .03, .7])).sum()), 1.)
        model = dict(theta=np.array([0., np.log(.02), np.log(4.)]), transitions=np.full(3, .1))
        r = np.array([[0., .1, .2, -.01, .03]])
        expected = np.array([[10., 20., 30., 40., 50.]])
        valid, order = np.ones_like(r, bool), [4, 2, 0, 3, 1]
        a = sequence_scores(model, r, expected, valid)
        b = sequence_scores(model, r[:, order], expected[:, order], valid[:, order])
        np.testing.assert_allclose(a, b, rtol=0., atol=1e-12)

    def test_delayed_density_matches_numerical_convolution(self):
        theta = np.array([.002, np.log(.025), np.log(4.)])
        for value in [-.05, .01, .2]:
            _, delayed, *_ = emissions(np.array([[value]]), np.array([[40.]]), theta)
            density = quad(lambda delay: norm.pdf(value-delay, loc=.002, scale=.025)*
                           np.exp(-delay/.1)/.1, 0., np.inf, epsabs=1e-11)[0]
            self.assertAlmostEqual(float(delayed[0, 0]), np.log(density), places=9)

    def test_original_labels_are_one_based(self):
        np.testing.assert_array_equal(anchor_mask([dict(known_sectors=[1, 5])]),
                                      [[True, False, False, False, True]])
        with self.assertRaises(ValueError):
            anchor_mask([dict(known_sectors=[0])])

    def test_emission_gradients(self):
        r = np.array([[-.02, .01, .6, 0., .04]])
        expected = np.array([[10., 20., 30., 50., 80.]])
        theta = np.array([-.003, np.log(.025), np.log(4.)])
        normal, incident, gn, gi = emissions(r, expected, theta)
        numerical = approx_derivative(lambda p: np.stack(emissions(r, expected, p)[:2]).ravel(), theta)
        np.testing.assert_allclose(numerical, np.stack([gn, gi]).reshape(-1, 3), atol=2e-6, rtol=2e-5)
        self.assertTrue(np.isfinite(normal).all() and np.isfinite(incident).all())

    def test_positive_bag_not_every_sector_forced(self):
        valid = np.ones((1, 5), bool)
        weights, _ = posterior(np.zeros((1, 5)), np.zeros((1, 5)), valid, [.1, .05, .6], [True])
        self.assertEqual(weights[0, 0], 0.)
        self.assertGreater(weights[0, 1], 0.)
        self.assertLess(float((weights @ PATHS)[0, 0]), 1.)
        anchors = np.array([[False, True, False, False, False]])
        anchored, _ = posterior(np.zeros((1, 5)), np.zeros((1, 5)), valid, [.1, .05, .6], [True], anchors)
        self.assertAlmostEqual(float((anchored @ PATHS)[0, 1]), 1.)

    def test_missing_is_marginalized_and_all_missing_unknown(self):
        valid = np.array([[True, False, True, True, False], [False]*5])
        a, _ = posterior(np.zeros((2, 5)), np.zeros((2, 5)), valid, [.1, .05, .6])
        b, _ = posterior(np.where(valid, 0., 1234.), np.where(valid, 0., -567.), valid, [.1, .05, .6])
        np.testing.assert_allclose(a, b)
        model = dict(theta=np.array([0., np.log(.02), np.log(4.)]), transitions=np.array([.1, .05, .6]))
        scores = sequence_scores(model, np.zeros((2, 5)), np.ones((2, 5))*30., valid)
        self.assertTrue(np.isfinite(scores[0]))
        self.assertTrue(np.isnan(scores[1]))

    def test_fit_and_isolated_loss_score(self):
        rng = np.random.default_rng(73)
        r = rng.normal(0., .01, (120, 5))
        y = np.zeros(120, bool)
        y[-20:] = True
        r[-20:, 2] += .3
        expected, valid = np.full_like(r, 30.), np.ones_like(r, bool)
        model = fit_sequence(r, expected, valid, y, iterations=8)
        values = sequence_scores(model, r, expected, valid)
        self.assertGreater(float(np.median(values[y])), float(np.median(values[~y])))
        self.assertTrue(np.isfinite(model['theta']).all())
        self.assertTrue(np.all((model['transitions'] > 0) & (model['transitions'] < 1)))

    def test_observations_use_quality_mask_and_historical_center_only_with_support(self):
        meta = [dict(key='a'), dict(key='b')]
        records = {'a': (np.array([30., 40., np.nan, 50., 60.]), np.array([30., 40., 0., 50., 60.]), None),
                   'b': (np.array([30., 40., 0., 50., 60.]), np.array([30., 40., 50., 50., 60.]), None)}
        history = np.zeros((2, 13))
        history[0, -3:] = [.1, .01, 3.]
        r, expected, valid = observations(records, meta, history)
        self.assertFalse(valid[0, 2] or valid[1, 2])
        self.assertAlmostEqual(expected[0, 0], 30.*np.exp(.1))
        self.assertAlmostEqual(r[1, 0], 0.)


if __name__ == '__main__':
    unittest.main()
