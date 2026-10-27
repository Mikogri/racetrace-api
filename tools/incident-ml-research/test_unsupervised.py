import unittest
import numpy as np
from unsupervised_runs import positive_profile, fit_anomaly, anomaly_scores


class UnsupervisedTests(unittest.TestCase):
    def test_one_sided_profile_keeps_loss_and_rank_paired(self):
        sectors = np.zeros((2, 17))
        sectors[:, 0] = [.1, .5]
        sectors[:, 1] = [1., 8.]
        sectors[:, 5] = [.8, .2]
        matrix = positive_profile(sectors)[:30].reshape(5, 6)
        np.testing.assert_allclose(matrix[0, [0, 1, 3]], [.5, 8., .2])

    def test_negative_losses_are_not_made_positive_and_missingness_is_explicit(self):
        sectors = np.zeros((2, 17))
        sectors[0, :8] = -2.
        sectors[1, 12] = 1.
        result = positive_profile(sectors)
        self.assertEqual(result[:30].sum(), 0.)
        self.assertAlmostEqual(result[30], .2)
        self.assertEqual(positive_profile(np.empty((0, 17)))[30], 0.)

    def test_score_direction_is_outlier_high_for_both_detector_families(self):
        fit = np.linspace(-.1, .1, 100).reshape(-1, 1)
        query = np.array([[0.], [10.]])
        for kind, parameter in [('isolation', 64), ('density', 10)]:
            model = fit_anomaly(fit, kind, parameter)
            scores = anomaly_scores(model, query)
            self.assertTrue(np.isfinite(scores).all())
            self.assertGreater(scores[1], scores[0], kind)


if __name__ == '__main__':
    unittest.main()
