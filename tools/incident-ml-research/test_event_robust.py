import unittest
import numpy as np
from scipy.optimize._numdiff import approx_derivative
import event_robust


class EventRiskTests(unittest.TestCase):
    def test_native_training_reloads_without_event_metadata_at_prediction(self):
        import lightgbm as lgb
        rng = np.random.default_rng(74)
        features = rng.normal(size=(400, 4))
        positive = features[:, 0] > .5
        events = np.array([str(i % 12) for i in range(400)])
        model = event_robust.fit_event_robust(features, positive, events, 7, 1., rounds=30)
        values = model.predict(features, raw_score=True, num_threads=2)
        self.assertGreater(float(np.median(values[positive])), float(np.median(values[~positive])))
        replay = lgb.Booster(model_str=model.model_to_string()).predict(features, raw_score=True, num_threads=2)
        np.testing.assert_array_equal(values, replay)

    def risk(self, scores, positive, events, strength):
        fn = getattr(event_robust, 'event_risk', None)
        self.assertTrue(callable(fn), 'Missing observed-positive event-risk objective')
        return fn(scores, positive, events, strength)

    def test_analytic_gradient_and_diagonal_curvature(self):
        s = np.array([.2, -.4, .9, .3, -.2])
        y, groups = np.array([True, True, True, False, False]), np.array(['a','a','b','a','b'])
        for strength in [0., 1., 5.]:
            with self.subTest(strength=strength):
                _, gradient, curvature = self.risk(s, y, groups, strength)
                ng = approx_derivative(lambda v: self.risk(v, y, groups, strength)[0], s).ravel()
                nh = np.diag(approx_derivative(lambda v: self.risk(v, y, groups, strength)[1], s))
                np.testing.assert_allclose(gradient, ng, rtol=1e-6, atol=1e-8)
                np.testing.assert_allclose(curvature, nh, rtol=1e-6, atol=1e-8)

    def test_static_mode_equalizes_positive_events_not_row_counts(self):
        value, gradient, curvature = self.risk(np.zeros(4), np.array([True,True,True,False]),
                                              np.array(['a','a','b','a']), 0.)
        self.assertAlmostEqual(value, 4*np.log(2))
        np.testing.assert_allclose(gradient, [-.25,-.25,-.5,1.])
        np.testing.assert_allclose(curvature, [.125,.125,.25,.5])

    def test_harder_positive_event_gets_more_gradient_but_unknown_events_are_not_used(self):
        s, y, groups = np.array([3., -3., .2, -.1]), np.array([True,True,False,False]), np.array(['a','b','a','b'])
        a = self.risk(s, y, groups, 5.)
        self.assertGreater(abs(a[1][1]), abs(a[1][0]))
        changed = groups.copy()
        changed[2:] = ['x','z']
        for expected, actual in zip(a, self.risk(s, y, changed, 5.)):
            np.testing.assert_array_equal(expected, actual)

    def test_extreme_margins_stay_finite_and_single_event_matches_balanced_logistic(self):
        value, gradient, curvature = self.risk(np.zeros(3), np.array([True,True,False]), np.array(['a','a','a']), 5.)
        self.assertAlmostEqual(value, 3*np.log(2))
        np.testing.assert_allclose(gradient, [-.375,-.375,.75])
        np.testing.assert_allclose(curvature, [.1875,.1875,.375])
        result = self.risk(np.array([-1000.,1000.]), np.array([True,False]), np.array(['a','a']), 5.)
        self.assertTrue(all(np.isfinite(v).all() for v in result))


if __name__ == '__main__':
    unittest.main()
