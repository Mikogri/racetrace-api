import unittest
import numpy as np
from lightgbm import LGBMRanker
from field_objectives import query_keys, event_weights, percentile_scores
from objectives import grouped_rows


class FieldObjectiveTests(unittest.TestCase):
    def test_queries_do_not_mix_sessions_or_categories(self):
        meta = [dict(key='10/1', group='2025/3', category='ME'),
                dict(key='11/1', group='2025/3', category='ME'),
                dict(key='12/2', group='2025/3', category='MJ'),
                dict(key='10/4', group='2025/3', category='ME')]
        self.assertEqual(query_keys(meta, 'session').tolist(), ['10', '11', '12', '10'])
        self.assertEqual(query_keys(meta, 'round-category').tolist(),
                         ['2025/3/ME', '2025/3/ME', '2025/3/MJ', '2025/3/ME'])
        order, sizes = grouped_rows(query_keys(meta, 'session'))
        self.assertEqual(sorted(order.tolist()), [0, 1, 2, 3])
        self.assertEqual(sizes.tolist(), [2, 1, 1])

    def test_duplicating_unknown_rows_does_not_multiply_event_influence(self):
        y = np.array([True, False, False, True, False])
        queries = np.array(['a', 'a', 'a', 'b', 'b'])
        weights = event_weights(y, queries)
        self.assertAlmostEqual(weights[y].sum(), 2.5)
        self.assertAlmostEqual(weights[~y].sum(), 2.5)
        self.assertAlmostEqual(weights[(queries == 'a') & ~y].sum(), 1.25)
        self.assertAlmostEqual(weights[(queries == 'b') & ~y].sum(), 1.25)

    def test_calibration_percentiles_handle_ties_and_do_not_use_test_distribution(self):
        calibration = np.array([1., 2., 2., 4.])
        np.testing.assert_allclose(percentile_scores(calibration, np.array([0., 2., 5.])), [0., .5, 1.])
        self.assertEqual(percentile_scores(calibration, np.array([2.]))[0], .5)

    def test_uniform_label_queries_are_not_silently_assumed_inert_for_xendcg(self):
        # Narrow characterization: XE-NDCG tie randomization still trains,
        # whereas LambdaRank has no unequal-label pairs in these queries.
        features = np.arange(20.).reshape(-1, 1)
        spreads = {}
        for objective in ['lambdarank', 'rank_xendcg']:
            model = LGBMRanker(objective=objective, n_estimators=10, num_leaves=3,
                min_child_samples=1, n_jobs=2, verbosity=-1, random_state=20261026, label_gain=[0, 1])
            model.fit(features, np.zeros(20, int), group=[10, 10])
            spreads[objective] = float(np.ptp(model.booster_.predict(features)))
        self.assertEqual(spreads['lambdarank'], 0.)
        self.assertGreater(spreads['rank_xendcg'], 0.)


if __name__ == '__main__':
    unittest.main()
