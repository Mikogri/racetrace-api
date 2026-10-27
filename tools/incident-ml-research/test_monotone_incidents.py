import unittest
import numpy as np
import monotone_incidents


class MonotoneTests(unittest.TestCase):
    def test_constraints_do_not_cover_support_or_context(self):
        build = getattr(monotone_incidents, 'constraints', None)
        self.assertTrue(callable(build), 'Missing physical-loss coordinate constraints')
        losses = build(100, False)
        ranks = build(100, True)
        self.assertEqual(np.flatnonzero(losses).tolist(), [0, 4, 8, 12])
        self.assertEqual(np.flatnonzero(ranks).tolist(), [0, 4, 8, 12, 20])
        self.assertTrue(np.all(losses >= 0) and np.all(ranks >= 0))

    def test_actual_tree_cannot_reduce_score_when_loss_coordinate_increases(self):
        fit = getattr(monotone_incidents, 'fit_monotone', None)
        self.assertTrue(callable(fit), 'Missing monotone tree fitting')
        rng = np.random.default_rng(94)
        features = rng.normal(size=(500, 75))
        positive = (features[:, 0]+rng.normal(scale=.3, size=500)) > .4
        model = fit(features, positive, 7, False, rounds=40)
        probes = np.zeros((100, 75))
        probes[:, 0] = np.linspace(-3., 3., 100)
        values = model.predict(probes)
        self.assertTrue(np.all(np.diff(values) >= -1e-12))
        self.assertGreater(values[-1], values[0])


if __name__ == '__main__':
    unittest.main()
