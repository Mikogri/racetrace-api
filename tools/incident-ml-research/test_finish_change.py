import unittest
import numpy as np
try:
    from finish_change import movements
except ImportError:
    movements = None


class FinishChangeTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(callable(movements), 'Derived finish-change experiment is not implemented')

    def test_large_last_sector_changes_order_within_the_same_completing_cohort(self):
        times = np.array([[10., 10., 10., 10., 50.], [11., 11., 11., 11., 11.],
                          [1., 1., 1., 1., np.nan]])
        values = movements(times, 4, True)
        self.assertEqual(values[0, 4, 0], 1.)
        self.assertEqual(values[0, 4, 2], 35.)
        self.assertEqual(values[0, 4, 6], 2.)
        self.assertEqual(movements(times, 4, False)[0, 4, 0], 0.)

    def test_missing_prefix_never_invents_a_finish_movement(self):
        times = np.array([[10., np.nan, 10., 10., 50.], [11., 11., 11., 11., 11.]])
        values = movements(times, 4, True)
        self.assertEqual(values[0, 4, 0], 0.)
        self.assertEqual(values[0, 4, 3], 0.)

    def test_four_sector_finish_keeps_padding_unknown(self):
        times = np.array([[10., 10., 10., 50., np.nan], [11., 11., 11., 11., np.nan]])
        values = movements(times, 3, True)
        self.assertEqual(values[0, 3, 0], 1.)
        self.assertTrue((values[:, 4] == 0).all())

    def test_common_clock_offset_does_not_create_a_rider_loss(self):
        times = np.array([[10., 10., 10., 10., 50.], [11., 11., 11., 11., 11.]])
        before = movements(times, 4, True)
        after = movements(times + 10., 4, True)
        np.testing.assert_allclose(before, after, atol=1e-12)


if __name__ == '__main__':
    unittest.main()
