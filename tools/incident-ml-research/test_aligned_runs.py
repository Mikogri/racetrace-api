import unittest
import numpy as np
from aligned_runs import sector_matrix, signed_log, fit_neural, neural_scores


class AlignedRunTests(unittest.TestCase):
    def test_time_and_rank_remain_on_the_same_sector_after_sorting(self):
        sectors = np.zeros((3, 17))
        sectors[:, 0] = [.1, .5, .2]
        sectors[:, 1] = [1., 8., 2.]
        sectors[:, 5] = [.8, .2, .6]
        sectors[:, 16] = [0., .25, .5]
        result = sector_matrix(sectors, sorted_losses=True)
        np.testing.assert_allclose(result[0, [0, 1, 5, 16]], [.5, 8., .2, .25])
        np.testing.assert_allclose(result[1, [1, 5]], [2., .6])

    def test_padding_is_missing_evidence_not_a_zero_time(self):
        sectors = np.zeros((1, 17))
        result = sector_matrix(sectors)
        np.testing.assert_array_equal(result[:, 12], [0., 1., 1., 1., 1.])
        self.assertEqual(sector_matrix(np.empty((0, 17)))[:, 12].sum(), 5.)

    def test_loss_sorted_representation_is_stable_under_row_permutation_and_ties(self):
        sectors = np.zeros((3, 17))
        sectors[:, 0] = [.1, .1, .2]
        sectors[:, 5] = [.2, .4, .3]
        sectors[:, 16] = [0., .25, .5]
        np.testing.assert_array_equal(sector_matrix(sectors, True),
                                      sector_matrix(sectors[[2, 1, 0]], True))

    def test_log_compression_keeps_sign_and_order_without_inventing_values(self):
        values = np.array([-1e6, -2., 0., 2., 1e6])
        actual = signed_log(values)
        self.assertTrue(np.isfinite(actual).all())
        self.assertTrue((np.diff(actual) > 0).all())
        self.assertEqual(actual[2], 0.)
        self.assertAlmostEqual(actual[0], -actual[-1])

    def test_neural_pipeline_preserves_equal_observed_class_mass(self):
        # With no signal, balanced class mass has optimum 0.5, not the raw
        # observed-label frequency 0.1. This catches dropping pipeline weights.
        features = np.zeros((1000, 2))
        positive = np.arange(1000) < 100
        model, _ = fit_neural(features, positive, (16, 'tanh', 10.))
        probability = neural_scores(model, np.zeros((1, 2)))[0]
        self.assertGreater(probability, .45)
        self.assertLess(probability, .55)


if __name__ == '__main__':
    unittest.main()
