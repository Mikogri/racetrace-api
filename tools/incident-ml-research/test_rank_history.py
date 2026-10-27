import unittest
import json
import tempfile
from pathlib import Path
import numpy as np
try:
    from rank_history import scoped_references, encode, swap_references
except ImportError:
    scoped_references = encode = swap_references = None


class RankHistoryTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(callable(scoped_references), 'Causal rank history is not implemented')
        self.meta = [dict(key=str(i)) for i in range(5)]
        self.records = {str(i): (None, None, None, 'A', f'2024-0{i+1}-01', str(i), 'ME') for i in range(5)}
        self.profiles = np.repeat(np.array([.1, .2, .3, .8, .9])[:, None], 5, axis=1)

    def test_rank_history_uses_prior_scoped_measurements_without_future(self):
        frozen, rolling = scoped_references(self.profiles, self.records, self.meta, [0], [1], [2, 3, 4])
        np.testing.assert_allclose(frozen[3], [.1, 0., 1.])
        np.testing.assert_allclose(rolling[3], [.2, .14826, 3.])
        self.profiles[4] = .01
        _, changed = scoped_references(self.profiles, self.records, self.meta, [0], [1], [2, 3, 4])
        np.testing.assert_array_equal(changed[3], rolling[3])

    def test_calibration_cannot_consume_an_earlier_test_record(self):
        records = self.records.copy()
        records['1'] = (*records['1'][:4], '2024-04-01', 'cal', 'ME')
        _, rolling = scoped_references(self.profiles, records, self.meta, [0], [1], [2, 3, 4])
        np.testing.assert_allclose(rolling[1], [.1, 0., 1.])

    def test_missing_rank_or_history_is_not_encoded_as_a_measured_fastest_sector(self):
        profiles = np.array([[0., np.nan, .5, .5, .5], [0., .1, .2, .3, .4]])
        values = encode(profiles, np.array([[.1, .1, 1.], [0., 0., 0.]]))
        self.assertAlmostEqual(values[0, 0], -.1)
        self.assertEqual(values[0, 10], 0.)
        self.assertEqual(values[0, 11], 1.)
        self.assertEqual(values[1, 10:15].tolist(), [1.]*5)

    def test_placebo_swaps_reference_only_within_matching_session_category_and_count(self):
        meta = [dict(key='1/A', category='ME'), dict(key='1/B', category='ME'), dict(key='2/C', category='ME')]
        reference = np.array([[.1, .02, 3.], [.8, .1, 3.], [.4, .1, 3.]])
        swapped, changed = swap_references(reference, meta)
        np.testing.assert_array_equal(swapped[0], reference[1])
        np.testing.assert_array_equal(swapped[:, -1], reference[:, -1])
        self.assertEqual(changed.tolist(), [True, True, False])

    def test_missing_or_invalid_raw_rank_is_unknown_not_a_middle_percentile(self):
        import rank_history
        load = getattr(rank_history, 'profiles_from_frozen', None)
        self.assertTrue(callable(load), 'Raw-rank quality validation is not implemented')
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for year in range(2021, 2027):
                path = root / f'artifacts/incident-multiseason/{year}'
                path.mkdir(parents=True)
                rows = [] if year != 2026 else [dict(SessionId=1, Run=dict(RiderId=2), Sectors=[
                    dict(SectorIndex=j, TimeSeconds=1., Rank=rank, Statistics=dict(Count=10))
                    for j, rank in enumerate([None, 1, 11])])]
                (path / f'inputs-{year}.json').write_text(json.dumps(dict(PaceSessions=rows)))
            profiles = load(root, [dict(key='1/2')], [np.zeros((3, 17))])
            self.assertTrue(np.isnan(profiles[0, 0]))
            self.assertEqual(profiles[0, 1], 0.)
            self.assertTrue(np.isnan(profiles[0, 2]))


if __name__ == '__main__':
    unittest.main()
