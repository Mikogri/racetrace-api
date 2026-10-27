import unittest
import numpy as np
try:
    from density_ratio import solve_nonnegative, prepare_ratio, fit_prepared, ratio_scores
except ImportError:
    solve_nonnegative = prepare_ratio = fit_prepared = ratio_scores = None


class DensityRatioTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(callable(solve_nonnegative), 'Nonnegative density-ratio learner is not implemented')

    def test_quadratic_solution_respects_the_nonnegative_boundary(self):
        coefficients = solve_nonnegative(np.diag([2., 1.]), np.array([2., -1.]))
        np.testing.assert_allclose(coefficients, [1., 0.], atol=1e-12)

    def test_ratio_learns_observed_positive_region_without_claiming_clean_controls(self):
        x = np.r_[np.linspace(-3., -2., 30), np.linspace(2., 3., 10)][:, None]
        observed = np.r_[np.zeros(30, bool), np.ones(10, bool)]
        prepared, transformed = prepare_ratio(x, observed, None)
        self.assertTrue(set(prepared['landmark_rows']) <= set(range(30, 40)))
        model = fit_prepared(prepared, transformed, observed, 1., .01)
        scores = ratio_scores(model, np.array([[-2.5], [2.5]]))
        self.assertGreater(scores[1], scores[0])
        self.assertTrue((model['coefficients'] >= 0).all())
        self.assertLess(model['kkt_residual'], 1e-7)

    def test_query_rows_cannot_refit_preprocessing_or_change_other_query_scores(self):
        x = np.arange(20., dtype=float).reshape(10, 2)
        observed = np.array([False]*7 + [True]*3)
        prepared, transformed = prepare_ratio(x, observed, 2)
        model = fit_prepared(prepared, transformed, observed, 1., .1)
        center = model['scaler'].center_.copy()
        before = ratio_scores(model, x[:2])
        after = ratio_scores(model, np.vstack([x[:2], [[1e9, -1e9]]]))
        np.testing.assert_allclose(before, after[:2], atol=1e-12)
        np.testing.assert_array_equal(center, model['scaler'].center_)

    def test_identical_profiles_remain_tied_and_finite(self):
        x = np.ones((4, 2))
        observed = np.array([True, True, False, False])
        prepared, transformed = prepare_ratio(x, observed, 12)
        model = fit_prepared(prepared, transformed, observed, 1., .1)
        scores = ratio_scores(model, x)
        self.assertTrue(np.isfinite(scores).all())
        np.testing.assert_array_equal(scores, np.repeat(scores[0], 4))


if __name__ == '__main__':
    unittest.main()
