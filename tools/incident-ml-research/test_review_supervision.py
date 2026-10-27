import unittest
import numpy as np
from review_supervision import training_labels


class ReviewSupervisionTests(unittest.TestCase):
    def test_review_labels_never_modify_calibration_or_test_targets(self):
        original = np.array([True, False, False, False])
        reviews = {1: dict(decision='yes', strict=True), 2: dict(decision='no', strict=True),
                   3: dict(decision='yes', strict=True)}
        target, clean = training_labels(original, np.array([0, 1]), reviews, promote=True)
        np.testing.assert_array_equal(target, [True, True, False, False])
        self.assertEqual(clean.sum(), 0)
        np.testing.assert_array_equal(original, [True, False, False, False])

    def test_unknown_and_outcome_only_reviews_are_not_strict_clean_labels(self):
        original = np.array([False, False, True])
        reviews = {0: dict(decision='unknown', strict=False),
                   1: dict(decision='no', strict=False),
                   2: dict(decision='no', strict=False)}
        target, clean = training_labels(original, np.arange(3), reviews, promote=True)
        np.testing.assert_array_equal(target, original)
        self.assertEqual(clean.sum(), 0)

    def test_negative_only_mode_keeps_unreported_positive_reviews_unlabelled(self):
        original = np.array([False, False])
        reviews = {0: dict(decision='yes', strict=True), 1: dict(decision='no', strict=True)}
        target, clean = training_labels(original, np.arange(2), reviews, promote=False)
        np.testing.assert_array_equal(target, original)
        np.testing.assert_array_equal(clean, [False, True])


if __name__ == '__main__':
    unittest.main()
