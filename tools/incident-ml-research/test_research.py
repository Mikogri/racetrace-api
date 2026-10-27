import unittest
import numpy as np
from data import sector_features, aggregate_features, explicit_sectors
from experiment import threshold_for_budget, evaluation_splits
from audit_inputs import same_measurements


class ResearchTests(unittest.TestCase):
    def test_one_good_sector_does_not_condemn_the_rest(self):
        f = sector_features([40, 50, 50, 50, 50], [50]*5, [.03]*5, [30]*5,
                            [1, 15, 15, 15, 15])
        self.assertEqual(int(np.argmax(f[:, 0])), 1)
        self.assertLess(float(np.max(f[:, 0])), .01)

    def test_isolated_loss_is_localized_without_run_label(self):
        f = sector_features([50, 50, 70, 50, 50], [50]*5, [.03]*5, [30]*5,
                            [10, 10, 29, 10, 10])
        self.assertEqual(int(np.argmax(f[:, 0])), 2)
        self.assertAlmostEqual(f[2, 1], 20)
        self.assertGreater(f[2, 6], 0)

    def test_missing_and_compensating_checkpoint_are_quality_not_crash_labels(self):
        f = sector_features([50, 80, 20, None, 50], [50]*5, [.03]*5, [30]*5,
                            [10, 29, 1, None, 10])
        self.assertEqual(f[3, 12], 1)
        self.assertEqual(f[1, 13], 1)
        self.assertEqual(f[2, 13], 1)
        self.assertTrue(np.isfinite(aggregate_features(f)).all())

    def test_sector_parser_does_not_confuse_split_with_sector(self):
        self.assertEqual(explicit_sectors('Upadek w sektorze 3.'), [3])
        self.assertEqual(explicit_sectors('Prowadził po splicie 2.'), [])

    def test_empty_run_is_retained_without_inventing_a_sector(self):
        f = sector_features([], [], [], [], [])
        self.assertTrue(np.isfinite(aggregate_features(f)).all())
        self.assertEqual(aggregate_features(f)[-2], 0)

    def test_missing_checkpoint_padding_is_not_a_different_measured_time(self):
        self.assertTrue(same_measurements([50, None, None], [50, None]))
        self.assertFalse(same_measurements([50, 10], [50]))

    def test_budget_threshold_does_not_use_test_scores(self):
        t = threshold_for_budget(np.array([.1, .2, .3, .4]), .25)
        self.assertEqual(int(np.sum(np.array([.1, .2, .3, .4]) > t)), 1)

    def test_outer_splits_separate_whole_years(self):
        years = np.repeat(np.arange(2021, 2027), 2)
        for name, train, test in evaluation_splits(years):
            self.assertFalse(set(years[train]) & set(years[test]), name)
        _, train, test = next(s for s in evaluation_splits(years) if s[0] == 'chronological-2025')
        self.assertEqual(max(years[train]), 2024)
        self.assertEqual(set(years[test]), {2025})


if __name__ == '__main__':
    unittest.main()
