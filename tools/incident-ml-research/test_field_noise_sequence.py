import unittest
import numpy as np
import field_noise_sequence
from sector_sequence import emissions, sequence_scores


class FieldNoiseTests(unittest.TestCase):
    def scale(self, observations, sectors, history=None):
        transform = getattr(field_noise_sequence, 'scale_observations', None)
        self.assertTrue(callable(transform), 'Missing field-dispersion observation adapter')
        return transform(observations, sectors, history)

    @staticmethod
    def field(mad, count=30):
        values = np.zeros((5, 17))
        values[:, 9], values[:, 10] = mad, count
        return values

    def test_scaling_preserves_actual_seconds(self):
        r, clocks, valid = np.full((2, 5), .1), np.full((2, 5), 30.), np.ones((2, 5), bool)
        a, e, v = self.scale((r, clocks, valid), [self.field(.02), self.field(.08)])
        np.testing.assert_allclose(a[0], .2)
        np.testing.assert_allclose(a[1], .05)
        np.testing.assert_allclose(a*e, r*clocks)
        np.testing.assert_array_equal(v, valid)

    def test_equal_loss_more_anomalous_in_tight_field(self):
        r, clocks, valid = np.zeros((2, 5)), np.full((2, 5), 30.), np.ones((2, 5), bool)
        r[:, 2] = .1
        view = self.scale((r, clocks, valid), [self.field(.02), self.field(.08)])
        model = dict(theta=np.array([0., np.log(.04), np.log(4.)]), transitions=np.array([.03, .03, .6]))
        scores = sequence_scores(model, *view)
        self.assertGreater(float(scores[0]), float(scores[1]))

    def test_unknown_low_support_and_invalid_dispersion_keep_neutral_scale(self):
        r, clocks, valid = np.full((3, 5), .1), np.full((3, 5), 30.), np.ones((3, 5), bool)
        fields = [self.field(.01, 3), self.field(np.nan), self.field(-1.)]
        a, e, v = self.scale((r, clocks, valid), fields)
        np.testing.assert_array_equal(a, r)
        np.testing.assert_array_equal(e, clocks)

    def test_history_spread_requires_actual_support(self):
        r, clocks, valid = np.full((2, 5), .1), np.full((2, 5), 30.), np.ones((2, 5), bool)
        history = np.zeros((2, 13))
        history[:, -2] = .03
        history[0, -1] = 4
        a, e, _ = self.scale((r, clocks, valid), [self.field(.04)]*2, history)
        np.testing.assert_allclose(a[0], .08)
        np.testing.assert_allclose(a[1], .1)
        np.testing.assert_allclose(a*e, r*clocks)


if __name__ == '__main__':
    unittest.main()
