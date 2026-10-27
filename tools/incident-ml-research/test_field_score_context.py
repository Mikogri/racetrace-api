import unittest
import numpy as np
from scipy.special import expit
from field_score_context import adjust_field_scores


class FieldScoreContextTests(unittest.TestCase):
    def test_zero_strength_preserves_margins_and_small_fields_are_untouched(self):
        raw = np.arange(-4., 4.)
        np.testing.assert_allclose(adjust_field_scores(expit(raw), ['a']*8, [0, 1, 2], 0), raw)
        np.testing.assert_allclose(adjust_field_scores(expit(raw), ['a']*4+['b']*4, [0, 1, 2], 1), raw)

    def test_leave_one_out_reference_and_shrinkage(self):
        raw = np.r_[np.zeros(7), 8., [-1., 0., 1.]]
        out = adjust_field_scores(expit(raw), ['a']*8+['b']*3, [8, 9, 10], 1)
        self.assertAlmostEqual(out[7], 8.)
        self.assertAlmostEqual(out[0], 0.)
        shifted = raw.copy()
        shifted[:8] += 2
        out = adjust_field_scores(expit(shifted), ['a']*8+['b']*3, [8, 9, 10], 1)
        self.assertAlmostEqual(out[7], 10.-2*7/27)

    def test_noncalibration_other_field_cannot_change_a_score(self):
        raw = np.r_[np.linspace(-1., 1., 8), np.zeros(8), np.arange(8.)]
        groups = ['a']*8+['b']*8+['c']*8
        first = adjust_field_scores(expit(raw), groups, np.arange(8, 16), 1, True)
        raw[16:] += 10
        second = adjust_field_scores(expit(raw), groups, np.arange(8, 16), 1, True)
        np.testing.assert_array_equal(first[:16], second[:16])

    def test_invalid_inputs(self):
        for p, cal, strength in [([np.nan], [0], 1), ([2.], [0], 1), ([.5], [0, 0], 1), ([.5], [0], 2)]:
            with self.assertRaises(ValueError):
                adjust_field_scores(p, ['a']*len(p), cal, strength)
