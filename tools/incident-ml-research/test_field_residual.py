import unittest
import numpy as np
from field_residual import loo_prediction


class FieldResidualTests(unittest.TestCase):
    def test_target_time_cannot_change_its_own_expected_time(self):
        x = np.column_stack([np.ones(5), np.arange(5.)])
        y = np.array([1., 2., 3., 4., 5.])
        original = loo_prediction(x, y)
        y[2] += 100.
        altered = loo_prediction(x, y)
        self.assertAlmostEqual(original[2], altered[2], places=10)

    def test_prediction_matches_explicit_leave_one_out_ridge_fit(self):
        x = np.column_stack([np.ones(5), np.arange(5.)])
        y = np.array([1., 2., 8., 4., 5.])
        actual = loo_prediction(x, y)
        penalty = np.diag([0., .1])
        for j in range(5):
            take = np.arange(5) != j
            beta = np.linalg.solve(x[take].T @ x[take]+penalty, x[take].T @ y[take])
            self.assertAlmostEqual(actual[j], x[j] @ beta, places=10)


if __name__ == '__main__':
    unittest.main()
