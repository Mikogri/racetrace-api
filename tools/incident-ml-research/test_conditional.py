import unittest
import numpy as np
from conditional import peer_reference, masked_context, goal_passed


class ConditionalTests(unittest.TestCase):
    def test_peer_reference_never_uses_the_target_sector_time(self):
        times=np.array([[50.,70.,50.],[50.,51.,50.],[51.,52.,50.],[49.,50.,51.]])
        first=peer_reference(times,0,1,10)[0]
        times[0,1]=500.
        self.assertEqual(peer_reference(times,0,1,10)[0],first)
        self.assertEqual(first,51.)

    def test_masked_context_excludes_target_measurement(self):
        times=np.array([50.,70.,50.,50.,50.])
        center=np.full(5,50.)
        first=masked_context(times,center,np.full(5,.03),np.full(5,30),1)
        times[1]=1000.
        np.testing.assert_array_equal(first,masked_context(times,center,np.full(5,.03),np.full(5,30),1))

    def test_goal_requires_both_recall_and_alarm_count(self):
        self.assertTrue(goal_passed(307,341,700))
        self.assertFalse(goal_passed(306,341,700))
        self.assertFalse(goal_passed(341,341,701))

    def test_missing_peers_do_not_invent_a_time(self):
        self.assertTrue(np.isnan(peer_reference(np.array([[50.,80.],[50.,np.nan]]),0,1,10)[0]))


if __name__ == '__main__':
    unittest.main()
