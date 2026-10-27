import unittest
import numpy as np
from paired_sessions import paired_reference, earlier_session


class PairedSessionTests(unittest.TestCase):
    def test_target_sector_time_cannot_change_its_reference(self):
        previous = np.full((4,5), 10.)
        current = np.full((4,5), 20.)
        expected, counts = paired_reference(previous, current, 0)
        current[0,2] = 100.
        altered, _ = paired_reference(previous, current, 0)
        self.assertAlmostEqual(expected[2], 20.)
        self.assertAlmostEqual(altered[2], 20.)
        self.assertEqual(counts[2], 3)

    def test_uniform_rider_change_is_not_a_local_incident(self):
        previous = np.full((4,5), 10.)
        current = np.full((4,5), 10.)
        current[0] = 12.
        expected, _ = paired_reference(previous, current, 0)
        self.assertTrue(np.allclose(expected, current[0]))

    def test_same_day_and_later_sessions_are_not_used(self):
        self.assertFalse(earlier_session('2026-05-02','2026-05-02'))
        self.assertFalse(earlier_session('2026-05-03','2026-05-02'))
        self.assertFalse(earlier_session(None,'2026-05-02'))
        self.assertTrue(earlier_session('2026-05-01','2026-05-02'))

    def test_missing_peer_measurements_do_not_invent_a_reference(self):
        previous = np.full((1,5), 10.)
        current = np.full((1,5), 12.)
        expected, counts = paired_reference(previous,current,0)
        self.assertTrue(np.isnan(expected).all())
        self.assertTrue(np.all(counts == 0))

    def test_change_profile_detects_local_not_uniform_session_slowdown(self):
        from paired_sessions import change_profile
        previous = np.full((4,5),10.)
        current = np.full((4,5),20.)
        current[0,2] = 30.
        values = change_profile(previous,current,0)
        self.assertAlmostEqual(values[2,1],10.)
        self.assertGreater(values[2,2],5.)
        self.assertEqual(values[2,3],3.)
        current[0,2] = 20.01
        small = change_profile(previous,current,0)
        self.assertLess(small[2,2],1.)
        self.assertEqual(small[2,3],3.)


if __name__ == '__main__':
    unittest.main()
