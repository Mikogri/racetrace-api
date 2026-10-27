import unittest
import numpy as np
from objectives import session_context


class StageContextTests(unittest.TestCase):
    def test_unseen_test_stage_cannot_create_a_training_feature(self):
        features, names = session_context([{'session': 'FINAL'}, {'session': 'QUALI'}, {'session': 'NEW'}], [0, 1])
        self.assertEqual(features.shape, (3, 2))
        self.assertTrue(np.array_equal(features[2], [0., 0.]))
        self.assertTrue(np.all(features[:2].sum(axis=1) == 1.))


if __name__ == '__main__':
    unittest.main()
