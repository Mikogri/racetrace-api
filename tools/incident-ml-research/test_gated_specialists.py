import unittest
import numpy as np
from sklearn.cluster import KMeans
from sklearn.preprocessing import RobustScaler
from threadpoolctl import threadpool_limits
import gated_specialists


class GateTests(unittest.TestCase):
    def gate(self, state, profiles, measured, ranks):
        fn = getattr(gated_specialists, 'gated_scores', None)
        self.assertTrue(callable(fn), 'Missing fit-state-only specialist routing')
        return fn(state, profiles, measured, ranks)

    def state(self):
        data = np.array([[-1.], [-1.], [1.], [1.]])
        scaler = RobustScaler().fit(data)
        with threadpool_limits(limits=2):
            clusterer = KMeans(n_clusters=2, init=np.array([[-.5], [.5]]), n_init=1,
                              random_state=2).fit(scaler.transform(data))
        return dict(scaler=scaler, clusterer=clusterer, groups=[np.array([0, 1]), np.array([2, 3])])

    def test_nearest_fit_profile_selects_its_expert_not_the_maximum(self):
        ranks = np.array([[.1, .9], [.8, .2]])
        values = self.gate(self.state(), np.array([[-1.], [1.]]), np.array([5, 5]), ranks)
        np.testing.assert_allclose(values, [.1, .2])
        np.testing.assert_array_equal(ranks, [[.1, .9], [.8, .2]])

    def test_low_support_keeps_the_archived_max_pooling_rule(self):
        values = self.gate(self.state(), np.array([[-1.], [1.]]), np.array([1, 5]),
                           np.array([[.1, .9], [.8, .2]]))
        np.testing.assert_allclose(values, [.8, .2])

    def test_single_profile_cluster_needs_no_fabricated_gate(self):
        values = self.gate(dict(clusterer=None, scaler=None, groups=[np.array([0])]),
                           np.array([[999.], [-999.]]), np.array([5, 0]), np.array([[.3, .7]]))
        np.testing.assert_allclose(values, [.3, .7])


if __name__ == '__main__':
    unittest.main()
