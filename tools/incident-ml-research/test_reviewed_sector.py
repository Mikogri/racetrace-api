import unittest
import csv
import tempfile
from pathlib import Path
import numpy as np
try:
    from reviewed_sector import prepare_fit, sample_controls
except ImportError:
    prepare_fit = sample_controls = None


class ReviewedSectorTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(callable(prepare_fit) and callable(sample_controls),
                        'Fit-restricted sector supervision is not implemented')

    def test_sector_anchors_never_cross_fit_boundary_or_mark_whole_run(self):
        original = np.array([True, False, False, True, False])
        known = [[1], [], [], [3], []]
        reviews = {1: dict(strict=True, conflict=False, decision='yes', sectors=[2, 4]),
                   2: dict(strict=True, conflict=False, decision='yes', sectors=[5]),
                   3: dict(strict=False, conflict=True, decision='no', sectors=[]),
                   4: dict(strict=False, conflict=False, decision='yes', sectors=[1])}
        eligible, target, negative, anchors = prepare_fit(original, known, np.array([0, 1, 3, 4]), reviews, True)
        self.assertEqual(eligible.tolist(), [0, 1, 4])
        self.assertEqual(target.tolist(), [True, True, False, True, False])
        self.assertEqual(anchors, [[1], [2, 4], [], [], []])
        self.assertFalse(negative.any())
        self.assertEqual(known, [[1], [], [], [3], []])
        self.assertEqual(original.tolist(), [True, False, False, True, False])

    def test_anchor_ablation_retains_same_promoted_runs_without_new_sector_labels(self):
        reviews = {0: dict(strict=True, conflict=False, decision='yes', sectors=[2]),
                   1: dict(strict=True, conflict=False, decision='no', sectors=[])}
        eligible, target, negative, anchors = prepare_fit(np.array([False, False]), [[], []],
                                                        np.arange(2), reviews, False)
        self.assertEqual(target.tolist(), [True, False])
        self.assertEqual(negative.tolist(), [False, True])
        self.assertEqual(anchors, [[], []])

    def test_reviewed_negative_sectors_are_kept_without_using_positive_siblings_as_clean(self):
        valid = np.ones((1003, 5), bool)
        positive = np.zeros(1003, bool)
        positive[0] = True
        negative = np.zeros(1003, bool)
        negative[-1] = True
        coordinates, weights = sample_controls(valid, positive, negative, 10., 1)
        self.assertFalse((coordinates[:, 0] == 0).any())
        self.assertEqual(coordinates[coordinates[:, 0] == 1002, 1].tolist(), [0, 1, 2, 3, 4])
        self.assertEqual(len(np.unique(coordinates, axis=0)), len(coordinates))
        self.assertAlmostEqual(weights.sum(), 1.)
        reviewed = weights[coordinates[:, 0] == 1002]
        other = weights[coordinates[:, 0] != 1002]
        self.assertAlmostEqual(reviewed[0] / other[0], 10.)

    def test_tuning_export_preserves_both_model_and_blend_columns(self):
        from reviewed_sector_search import write_tuning
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'tuning.csv'
            write_tuning(path, [dict(model='sector', leaves=7), dict(model='blend', weight=.5)])
            with path.open() as handle:
                rows = list(csv.DictReader(handle))
            self.assertEqual(rows, [dict(model='sector', leaves='7', weight=''),
                                    dict(model='blend', leaves='', weight='0.5')])


if __name__ == '__main__':
    unittest.main()
