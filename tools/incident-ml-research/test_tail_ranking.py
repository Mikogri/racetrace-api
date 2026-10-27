import unittest
import math
import numpy as np
from scipy.optimize._numdiff import approx_derivative
import tail_ranking


class TailRankingTests(unittest.TestCase):
    def test_full_pair_chunks_accumulate_against_literal_pair_sum(self):
        # More than 256 U rows crosses the objective's chunk boundary.
        scores = np.concatenate([np.arange(600)/600., [-.1, .2]])
        positive = np.arange(602) >= 600
        value, gradient, curvature = self.objective(scores, positive, 1.)
        expected_loss, expected_g, expected_h = 0., np.zeros(602), np.zeros(602)
        factor = 602/1200.
        for p in [600, 601]:
            for u in range(600):
                delta = float(scores[u]-scores[p])
                probability = 1/(1+math.exp(-delta))
                expected_loss += factor*math.log1p(math.exp(delta))
                expected_g[p] -= factor*probability
                expected_g[u] += factor*probability
                expected_h[p] += factor*probability*(1-probability)
                expected_h[u] += factor*probability*(1-probability)
        self.assertAlmostEqual(value, expected_loss, places=10)
        np.testing.assert_allclose(gradient, expected_g, rtol=0., atol=1e-10)
        np.testing.assert_allclose(curvature, expected_h, rtol=0., atol=1e-10)

    def test_native_booster_learns_and_reloads_without_training_callback(self):
        import lightgbm as lgb
        rng = np.random.default_rng(29)
        features = rng.normal(size=(400, 3))
        positive = features[:, 0] > 1.
        model = tail_ranking.fit_tail(features, positive, 7, .1, rounds=30)
        values = tail_ranking.tail_scores(model, features)
        self.assertGreater(float(np.median(values[positive])), float(np.median(values[~positive])))
        replay = tail_ranking.tail_scores(lgb.Booster(model_str=model.model_to_string()), features)
        np.testing.assert_array_equal(values, replay)

    def objective(self, scores, positive, fraction):
        fn = getattr(tail_ranking, 'tail_objective', None)
        self.assertTrue(callable(fn), 'Missing upper-tail observed-label ranking objective')
        return fn(scores, positive, fraction)

    def test_gradient_and_diagonal_curvature_away_from_cutoff_ties(self):
        scores = np.array([.3, -.8, .9, .1, .6])
        positive = np.array([True, False, False, True, False])
        value, gradient, curvature = self.objective(scores, positive, .5)
        expected_g = approx_derivative(lambda s: self.objective(s, positive, .5)[0], scores).ravel()
        expected_h = np.diag(approx_derivative(lambda s: self.objective(s, positive, .5)[1], scores))
        np.testing.assert_allclose(gradient, expected_g, atol=1e-8, rtol=1e-7)
        np.testing.assert_allclose(curvature, expected_h, atol=1e-8, rtol=1e-7)
        self.assertEqual(gradient[1], 0.)
        self.assertEqual(curvature[1], 0.)

    def test_boundary_ties_get_fractional_symmetric_weight(self):
        value, gradient, curvature = self.objective(np.zeros(3), np.array([True, False, False]), .5)
        self.assertAlmostEqual(value, 3*np.log(2))
        np.testing.assert_allclose(gradient, [-1.5, .75, .75])
        np.testing.assert_allclose(curvature, [.75, .375, .375])

    def test_translation_and_row_permutation_invariance(self):
        s, y = np.array([.2, .8, .8, -.3]), np.array([True, False, False, True])
        a = self.objective(s, y, .4)
        shifted = self.objective(s+23., y, .4)
        for left, right in zip(a, shifted):
            np.testing.assert_allclose(left, right, atol=1e-12, rtol=0.)
        order = [2, 0, 3, 1]
        b = self.objective(s[order], y[order], .4)
        self.assertAlmostEqual(a[0], b[0])
        np.testing.assert_allclose(a[1][order], b[1])


if __name__ == '__main__':
    unittest.main()
