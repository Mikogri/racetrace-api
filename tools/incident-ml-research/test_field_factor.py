import unittest
import numpy as np
try:
    from field_factor import field_delays
except ImportError:
    field_delays = None


class FieldFactorTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(callable(field_delays), 'Field factor decomposition is not implemented')
        self.times = np.linspace(1., 2., 20)[:, None] * np.array([10., 20., 30., 40., 50.])

    def test_uniform_slower_runs_do_not_become_local_incidents(self):
        values = field_delays(self.times, np.zeros(20), 1, 1.5)
        self.assertLess(np.nanmax(np.abs(values[:, :, 0])), 1e-8)

    def test_isolated_loss_is_localized_and_not_absorbed_as_global_tempo(self):
        self.times[0, 2] += 20.
        values = field_delays(self.times, np.zeros(20), 1, 1.5)
        self.assertEqual(int(np.nanargmax(values[0, :, 0])), 2)
        self.assertGreater(values[0, 2, 0], .2)
        self.assertLess(np.nanmax(values[1:, :, 0]), .02)

    def test_missing_timing_is_unknown_not_a_measured_zero(self):
        self.times[0, 3] = np.nan
        self.times[1] = np.nan
        values = field_delays(self.times, np.zeros(20), 1, 1.5)
        self.assertTrue(np.isnan(values[0, 3]).all())
        self.assertTrue(np.isnan(values[1]).all())
        self.assertTrue(np.isfinite(values[0, :3]).all())

    def test_other_sessions_cannot_change_the_current_field(self):
        times = np.vstack([self.times, self.times * 3.])
        groups = np.repeat([0, 1], 20)
        before = field_delays(times, groups, 1, 1.5)
        times[20:] *= np.arange(1., 6.)
        after = field_delays(times, groups, 1, 1.5)
        np.testing.assert_array_equal(before[:20], after[:20])


if __name__ == '__main__':
    unittest.main()
