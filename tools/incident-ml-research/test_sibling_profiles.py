import unittest
import numpy as np
try:
    from sibling_profiles import references, placebo, features
except ImportError:
    references = placebo = features = None


class SiblingProfileTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(callable(references), 'Sibling-session profiles are not implemented')
        self.meta = [dict(key='1/A', group='2026/1', category='ME'),
                     dict(key='2/A', group='2026/1', category='ME'),
                     dict(key='3/A', group='2026/2', category='ME'),
                     dict(key='4/A', group='2026/1', category='WE')]

    def test_reference_excludes_target_and_other_round_category_or_course(self):
        profiles = np.array([[2.]*5, [1.]*5, [9.]*5, [8.]*5])
        expected, counts = references(profiles, self.meta, ['track/5']*4)
        np.testing.assert_array_equal(expected[0], [1.]*5)
        np.testing.assert_array_equal(counts[0], [1]*5)
        profiles[0] *= 100.
        altered, _ = references(profiles, self.meta, ['track/5']*4)
        np.testing.assert_array_equal(altered[0], expected[0])
        different, _ = references(profiles, self.meta, ['track/5', 'other/5', 'track/5', 'track/5'])
        self.assertTrue(np.isnan(different[0]).all())

    def test_missing_counterpart_measurements_remain_unknown(self):
        profiles = np.array([[0.]*5, [np.nan, 1., 1., 1., 1.], [0.]*5, [0.]*5])
        reference, counts = references(profiles, self.meta, ['track/5']*4)
        self.assertTrue(np.isnan(reference[0, 0]))
        self.assertEqual(counts[0].tolist(), [0, 1, 1, 1, 1])

    def test_placebo_changes_person_without_changing_reference_support(self):
        meta = [dict(key='1/A', group='2026/1', category='ME'),
                dict(key='1/B', group='2026/1', category='ME'),
                dict(key='2/C', group='2026/1', category='ME')]
        reference = np.array([[np.nan, 1., 1., 1., 1.], [np.nan, 2., 2., 2., 2.], [np.nan, 3., 3., 3., 3.]])
        swapped, changed = placebo(reference, np.ones((3, 5), int), meta, ['t']*3)
        np.testing.assert_array_equal(swapped[0], reference[1])
        np.testing.assert_array_equal(np.isnan(swapped), np.isnan(reference))
        self.assertEqual(changed.tolist(), [True, True, False])

    def test_persistent_loss_is_not_removed_from_all_features_by_centering(self):
        profile = np.ones((1, 5)) * np.log(1.2)
        values = features(profile, np.zeros((1, 5)), np.ones((1, 5)), np.ones((1, 5))*10.)
        self.assertGreater(values[0, 0], .18)
        self.assertAlmostEqual(values[0, 2], 2.)


if __name__ == '__main__':
    unittest.main()
