import unittest
import numpy as np
from hybrid_search import training_weights


class HybridTests(unittest.TestCase):
    def test_emphasizing_suspicious_controls_keeps_total_class_mass_balanced(self):
        y = np.array([True, False, False])
        weights = training_weights(y,np.array([True,True,False]),3.)
        self.assertAlmostEqual(weights[y].sum(),weights[~y].sum())
        self.assertAlmostEqual(weights[1]/weights[2],3.)

    def test_nonfinite_control_weight_is_rejected(self):
        with self.assertRaises(ValueError):
            training_weights(np.array([True,False]),np.array([False,True]),float('nan'))


if __name__ == '__main__':
    unittest.main()
