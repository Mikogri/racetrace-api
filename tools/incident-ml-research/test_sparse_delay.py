import unittest
import numpy as np
from sparse_delay import delay_features, fit_covariance


class SparseDelayTests(unittest.TestCase):
    def test_single_positive_delay_matches_hand_solved_intercept_fit(self):
        # Identity noise, five sectors: M[j,j]=4/5, v'My=8/5.
        result = delay_features(np.array([[0., 0., 2., 0., 0.]]), np.eye(5))
        self.assertAlmostEqual(result[0, 2, 0], 1.6)
        self.assertAlmostEqual(result[0, 2, 1], 2.)
        self.assertAlmostEqual(result[0, 2, 2], 3.2)

    def test_uniform_pace_shift_cannot_create_or_change_local_delay(self):
        profiles = np.array([[0., .1, .02, .01, 0.], [.2, .2, .2, .2, .2]])
        actual = delay_features(profiles, np.eye(5))
        shifted = delay_features(profiles + np.array([[4.], [-2.]]), np.eye(5))
        np.testing.assert_allclose(actual, shifted, atol=1e-12)
        self.assertLess(actual[1].max(), 1e-12)

    def test_fast_sector_is_not_a_positive_delay_on_that_sector(self):
        result = delay_features(np.array([[0., 0., -2., 0., 0.]]), np.eye(5))
        self.assertEqual(result[0, 2, 0], 0.)
        self.assertEqual(result[0, 2, 1], 0.)

    def test_two_sector_delay_is_fitted_as_one_contiguous_episode(self):
        profile = np.array([[0., 2., 2., 0., 0.]])
        isolated = delay_features(profile, np.eye(5))
        episode = delay_features(profile, np.eye(5), max_length=2)
        self.assertAlmostEqual(episode[0, 1, 0], 2.4)
        self.assertAlmostEqual(episode[0, 1, 1], 2.)
        self.assertGreater(episode[0, 1, 0], isolated[0, 1, 0])

    def test_smooth_tempo_trend_is_nuisance_only_when_requested(self):
        profile = np.array([[0., 1., 2., 3., 4.]])
        self.assertGreater(delay_features(profile, np.eye(5)).max(), 0.)
        self.assertLess(np.abs(delay_features(profile, np.eye(5), trend=True)).max(), 1e-10)

    def test_missing_sector_uses_marginal_covariance_not_zero_imputation(self):
        covariance = np.eye(5)
        covariance[0, 4] = covariance[4, 0] = .9
        profile = np.array([[0., 2., np.nan, np.nan, np.nan]])
        # Observed 2x2 covariance is identity regardless of correlation with missing S5.
        result = delay_features(profile, covariance)
        self.assertAlmostEqual(result[0, 1, 0], 1.)
        self.assertAlmostEqual(result[0, 1, 1], 2.)
        self.assertEqual(result[0, 4].sum(), 0.)

    def test_one_measurement_has_no_identifiable_local_delay(self):
        result = delay_features(np.array([[np.nan, 3., np.nan, np.nan, np.nan],
                                          [np.nan] * 5]), np.eye(5))
        self.assertTrue(np.isfinite(result).all())
        self.assertEqual(result.sum(), 0.)

    def test_covariance_fit_is_pace_shift_invariant_and_positive_definite(self):
        rng = np.random.default_rng(7)
        profiles = rng.normal(0., .02, (200, 5))
        first = fit_covariance(profiles, .8)
        second = fit_covariance(profiles + rng.normal(0., 2., (200, 1)), .8)
        np.testing.assert_allclose(first, second, atol=1e-12)
        self.assertGreater(np.linalg.eigvalsh(first).min(), 0.)


if __name__ == '__main__':
    unittest.main()
