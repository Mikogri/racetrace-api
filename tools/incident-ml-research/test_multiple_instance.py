import unittest
import numpy as np
from multiple_instance import choose_instances, bag_scores, noisy_or_loss_gradient


class MultipleInstanceTests(unittest.TestCase):
    def test_run_label_selects_one_latent_sector_not_all_sectors(self):
        selected = choose_instances(np.array([[.1, .8, .3]]), np.ones((1, 3), bool), [[]])
        self.assertEqual(selected.tolist(), [[False, True, False]])

    def test_explicit_sector_is_used_only_when_valid(self):
        values = np.array([[.9, .1, .3], [.9, .1, .3]])
        valid = np.array([[True, True, True], [True, False, True]])
        selected = choose_instances(values, valid, [[2], [2]])
        self.assertEqual(selected.tolist(), [[False, True, False], [True, False, False]])

    def test_empty_bag_does_not_invent_an_incident(self):
        scores = bag_scores(np.array([[.9, .8]]), np.zeros((1, 2), bool))
        self.assertEqual(scores.tolist(), [0.])

    def test_bag_gradient_matches_finite_difference_without_all_sector_labels(self):
        raw = np.array([-.8, .4, -1.2, .2])
        bags = np.array([0, 0, 1, 1])
        labels = np.array([True, False])
        loss, gradient, hessian = noisy_or_loss_gradient(raw, bags, labels)
        for j in range(len(raw)):
            plus, minus = raw.copy(), raw.copy()
            plus[j] += 1e-5
            minus[j] -= 1e-5
            numeric = (noisy_or_loss_gradient(plus, bags, labels)[0]-
                       noisy_or_loss_gradient(minus, bags, labels)[0])/2e-5
            self.assertAlmostEqual(gradient[j], numeric, places=6)
        self.assertTrue(np.all(hessian > 0))
        self.assertTrue(np.isfinite(loss))


if __name__ == '__main__':
    unittest.main()
