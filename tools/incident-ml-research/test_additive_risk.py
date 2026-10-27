import pickle
import unittest
import numpy as np
from additive_risk import fit_basis, transform_basis, fit_additive


class AdditiveRiskTests(unittest.TestCase):
    def test_fit_only_knots_constant_columns_and_binary_indicators(self):
        x = np.c_[np.arange(20.), np.ones(20), np.tile([0., 1.], 10)]
        basis = fit_basis(x)
        self.assertEqual([b['column'] for b in basis], [0, 2])
        self.assertEqual(len(basis[0]['knots']), 3)
        self.assertEqual(len(basis[1]['knots']), 0)
        self.assertEqual(transform_basis(x, basis).shape, (20, 5))
        before = pickle.dumps(basis)
        self.assertTrue(np.isfinite(transform_basis(np.array([[1e20, 5., 2.]]), basis)).all())
        self.assertEqual(before, pickle.dumps(basis))

    def test_continuous_piecewise_linear_basis_and_additive_no_interactions(self):
        x = np.c_[np.arange(1., 25.), np.arange(1., 25.)**2]
        basis = fit_basis(x)
        # In signed-log space every column's terminal extension is linear.
        z = np.array([[30., 30.], [31., 30.], [30., 31.], [31., 31.]])
        values = transform_basis(np.expm1(z), basis)
        np.testing.assert_allclose(values[3]-values[2], values[1]-values[0], atol=1e-12)
        extreme = transform_basis(np.expm1(np.array([[30., 30.], [31., 30.], [32., 30.]])), basis)
        np.testing.assert_allclose(extreme[2]-extreme[1], extreme[1]-extreme[0], atol=1e-12)

    def test_model_reloads_and_uses_no_labels_at_prediction(self):
        x = np.c_[np.linspace(-2, 2, 240), np.tile([0., 1.], 120)]
        y = x[:, 0] > 1
        model, diagnostics = fit_additive(transform_basis(x, fit_basis(x)), y, .1)
        score = model.decision_function(transform_basis(x, fit_basis(x)))
        self.assertGreater(np.median(score[y]), np.median(score[~y]))
        np.testing.assert_array_equal(score, pickle.loads(pickle.dumps(model)).decision_function(transform_basis(x, fit_basis(x))))
        self.assertEqual(diagnostics['convergence_warnings'], 0)

    def test_rejects_invalid_fit_data(self):
        for x in [np.array([[np.nan]]), np.empty((0, 2)), np.array([1., 2.])]:
            with self.assertRaises(ValueError):
                fit_basis(x)

    def test_rare_feature_has_variance_scale_even_when_central_quantiles_are_zero(self):
        x = np.zeros((1000, 1))
        x[-1] = .001
        basis = fit_basis(x)
        out = transform_basis(x, basis)
        self.assertLessEqual(float(np.max(np.abs(out))), np.sqrt(len(x))+1)
