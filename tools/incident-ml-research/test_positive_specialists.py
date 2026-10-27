import unittest
import numpy as np
import positive_specialists


class SpecialistTests(unittest.TestCase):
    def test_profiles_center_only_valid_measurements_and_keep_missing_masks(self):
        fn = getattr(positive_specialists, 'profiles', None)
        self.assertTrue(callable(fn), 'Missing quality-masked relative sector profile')
        sectors = np.zeros((3, 17))
        sectors[:, 4] = [.1, .2, 999.]
        sectors[2, 13] = 1
        shape, availability, count = fn([sectors])
        np.testing.assert_allclose(shape[0], [-.05, .05, 0, 0, 0, 0, 0, 1, 1, 1], atol=1e-12)
        np.testing.assert_array_equal(availability[0], [0, 0, 1, 1, 1])
        self.assertEqual(count.tolist(), [2])

    def test_cluster_centres_and_groups_ignore_every_held_row_and_label(self):
        fn = getattr(positive_specialists, 'partition_positives', None)
        self.assertTrue(callable(fn), 'Missing fit-only positive subtype grouping')
        values = np.random.default_rng(25).normal(size=(9, 10))
        measured, y = np.full(9, 5), np.array([True]*7+[False]*2)
        measured[5] = 1
        eligible = np.array([0, 1, 2, 3, 4, 5, 7, 8])
        a = fn(values, measured, y, eligible)
        changed = values.copy()
        changed[6] = 9999.
        target = y.copy()
        target[6] = False
        b = fn(changed, measured, target, eligible)
        np.testing.assert_array_equal(a['scaler'].center_, b['scaler'].center_)
        np.testing.assert_array_equal(a['clusterer'].cluster_centers_, b['clusterer'].cluster_centers_)
        self.assertEqual([g.tolist() for g in a['groups']], [g.tolist() for g in b['groups']])
        self.assertEqual(set(np.concatenate(a['groups']).tolist()), set(range(6)))
        self.assertTrue(all(5 in group and len(group) == len(np.unique(group)) for group in a['groups']))

    def test_single_sector_positives_are_shared_not_dropped_or_forced_into_a_shape(self):
        fn = getattr(positive_specialists, 'partition_positives', None)
        self.assertTrue(callable(fn), 'Missing fit-only positive subtype grouping')
        result = fn(np.zeros((3, 10)), np.array([1, 0, 5]), np.array([True, True, False]), np.arange(3))
        self.assertEqual([g.tolist() for g in result['groups']], [[0, 1]])
        self.assertIsNone(result['clusterer'])


if __name__ == '__main__':
    unittest.main()
