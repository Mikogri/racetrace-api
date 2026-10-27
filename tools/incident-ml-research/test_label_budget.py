import unittest
import numpy as np
import label_budget


class LabelBudgetTests(unittest.TestCase):
    def sample(self, y, groups, eligible, fraction, unit='run'):
        fn = getattr(label_budget, 'sample_fit_labels', None)
        self.assertTrue(callable(fn), 'Missing fit-only nested label sampler')
        return fn(y, groups, eligible, fraction, 24, unit)

    def test_subsets_are_nested_keep_every_u_and_do_not_modify_labels(self):
        y = np.array([True]*8+[False]*3+[True])
        before = y.copy()
        groups = np.array(['a','a','b','b','c','c','d','d','x','x','x','held'])
        eligible = np.arange(11)
        small = self.sample(y, groups, eligible, .25)
        half = self.sample(y, groups, eligible, .5)
        self.assertTrue(set(small) < set(half))
        self.assertEqual(set(small[~y[small]]), {8,9,10})
        self.assertEqual(int(y[small].sum()), 2)
        self.assertNotIn(11, half)
        np.testing.assert_array_equal(y, before)

    def test_event_sampling_keeps_or_excludes_all_p_of_the_selected_event(self):
        y = np.array([True]*6+[False,False])
        groups = np.array(['a','a','b','b','c','c','a','c'])
        selected = self.sample(y, groups, np.arange(8), 1/3, 'event')
        self.assertEqual(int(y[selected].sum()), 2)
        self.assertEqual(len(set(groups[selected[y[selected]]])), 1)
        self.assertTrue({6,7} <= set(selected))

    def test_full_budget_preserves_exact_fit_row_order_and_held_labels_cannot_influence_sampling(self):
        y = np.array([True, False, True, True])
        groups, eligible = np.array(['a','b','c','held']), np.array([2,0,1])
        np.testing.assert_array_equal(self.sample(y, groups, eligible, 1.), eligible)
        a = self.sample(y, groups, eligible, .5)
        y[3], groups[3] = False, 'new'
        np.testing.assert_array_equal(a, self.sample(y, groups, eligible, .5))


if __name__ == '__main__':
    unittest.main()
