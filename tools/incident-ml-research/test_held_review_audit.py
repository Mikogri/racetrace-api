import unittest
import numpy as np
import held_review_audit


class HeldReviewTests(unittest.TestCase):
    def counts(self, meta, reviews, indices, flagged):
        fn = getattr(held_review_audit, 'review_counts', None)
        self.assertTrue(callable(fn), 'Missing strictly held-row auxiliary review audit')
        return fn(meta, reviews, np.array(indices), np.array(flagged))

    def test_only_held_rows_count_and_conflicts_are_separate(self):
        meta = [dict(positive=False), dict(positive=False), dict(positive=True)]
        reviews = {0: dict(strict=True, decision='yes', conflict=False),
                   1: dict(strict=True, decision='no', conflict=False),
                   2: dict(strict=False, decision='no', conflict=True)}
        result = self.counts(meta, reviews, [0, 2], [True, False])
        self.assertEqual(result, dict(added_positive_reviews=1, added_positive_alerts=1,
            negative_reviews=0, negative_alerts=0, disputed_original_positives=1, disputed_alerts=0))
        self.assertEqual(meta[2]['positive'], True)

    def test_outcome_only_unknown_and_existing_positives_not_promoted_in_auxiliary_count(self):
        meta = [dict(positive=False), dict(positive=True), dict(positive=False)]
        reviews = {0: dict(strict=False, decision='yes', conflict=False),
                   1: dict(strict=True, decision='yes', conflict=False),
                   2: dict(strict=True, decision='no', conflict=False)}
        result = self.counts(meta, reviews, [0, 1, 2], [True, True, True])
        self.assertEqual(result['added_positive_reviews'], 0)
        self.assertEqual(result['negative_reviews'], 1)
        self.assertEqual(result['negative_alerts'], 1)


if __name__ == '__main__':
    unittest.main()
