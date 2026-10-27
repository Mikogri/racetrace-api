import unittest
import numpy as np
from environment import speed_features


class EnvironmentTests(unittest.TestCase):
    def test_relative_speed_features_do_not_depend_on_speed_unit(self):
        speeds = np.array([35.,40.,45.,50.])
        ability = np.array([.1,.05,0.,-.05])
        a = speed_features(speeds,ability)
        b = speed_features(speeds*3.6,ability)
        self.assertTrue(np.allclose(a,b,atol=1e-10))

    def test_missing_speed_is_not_a_measured_zero_speed(self):
        f = speed_features(np.array([35.,40.,np.nan,-1.]),np.zeros(4))
        self.assertEqual(f[2,0],1.)
        self.assertEqual(f[3,0],1.)
        self.assertTrue(np.all(f[2,2:7] == 0.))
        self.assertTrue(np.isfinite(f).all())


if __name__ == '__main__':
    unittest.main()
