import random
import unittest
from wide_search import candidate_configs


class WideSearchTests(unittest.TestCase):
    def test_grid_is_reproducible_without_mutating_global_random_state(self):
        state=random.getstate()
        self.assertEqual(candidate_configs(123,10),candidate_configs(123,10))
        self.assertEqual(random.getstate(),state)

    def test_requested_configs_are_distinct(self):
        values=candidate_configs(123,48)
        self.assertEqual(len({tuple(sorted(r.items())) for r in values}),48)


if __name__=='__main__':
    unittest.main()
