import unittest
import numpy as np
from data import sector_features
from focus import sequence_features, relative_columns


class FocusTests(unittest.TestCase):
    def test_time_and_rank_contrast_stay_on_the_same_candidate(self):
        f = sector_features([50,70,50,50,50], [50]*5, [.03]*5, [30]*5,
                            [10,29,10,10,10])
        result=sequence_features(f)
        self.assertAlmostEqual(result[1],20)
        self.assertGreater(result[4],.5)

    def test_empty_measurements_do_not_invent_a_candidate(self):
        self.assertTrue(np.isfinite(sequence_features(np.zeros((0,17)))).all())
        self.assertEqual(sequence_features(np.zeros((0,17))).shape,(28,))

    def test_absolute_field_context_is_excluded(self):
        cols=relative_columns(['sample_count_max','duration_min','log_excess_max','field_log_ratio_std','rank_percentile_min'])
        self.assertEqual(cols,[2,3])


if __name__ == '__main__':
    unittest.main()
