"""Checks for the offline, positive-only incident experiment."""
import importlib.util
import sys
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('multiseason', Path(__file__).with_name('incident-multiseason.py'))
research = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = research
spec.loader.exec_module(research)


class MatchingTests(unittest.TestCase):
    def test_only_incident_rows_are_read_and_narratives_are_not_retained(self):
        text = '''### Snowshoe #2
| ID | Data | Zawodnik | Kategoria | Sesja | Typ | Opis | Źródło |
| 7 | 2021-09-18 | Loïc Bruni | Elite Men | FINAL | `CRASH` | private narrative | external link |
'''
        rows = research.parse_markdown(text, 2021)
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]['category'], 'EM')
        self.assertEqual(rows[0]['session_types'], (0,))
        self.assertNotIn('private narrative', str(rows))

    def test_ambiguous_qualifying_is_not_assigned_to_q1_or_q2(self):
        self.assertIsNone(research.session_types('Qualifying (Q?)'))
        self.assertIsNone(research.session_types('QUALIFYING / PRACTICE'))
        self.assertEqual(research.session_types('Semi-final'), (1,))

    def test_duplicate_observations_count_as_one_positive_run(self):
        positives = research.unique_positives([{'key': ('a', 'b')}, {'key': ('a', 'b')}, {'key': None}])
        self.assertEqual(positives, {('a', 'b')})

    def test_snowshoe_rounds_and_championships_remain_distinct(self):
        row = {'season': 2021, 'event': 'Snowshoe #2'}
        self.assertFalse(research.event_matches(row, {'tracks': {'venue': 'Snowshoe'}, 'round_number': 5,
                                                     'competition_levels': {'code': 'world_cup'}}))
        self.assertTrue(research.event_matches(row, {'tracks': {'venue': 'Snowshoe'}, 'round_number': 6,
                                                    'competition_levels': {'code': 'world_cup'}}))
        row = {'season': 2022, 'event': 'Les Gets (WCH)'}
        self.assertFalse(research.event_matches(row, {'tracks': {'venue': 'Les Gets'}, 'round_number': 1,
                                                     'competition_levels': {'code': 'world_cup'}}))

    def test_model_selection_obeys_alarm_budget_and_ignores_held_out_labels(self):
        results = [('abstain', set()), ('conservative', {1}), ('sensitive', {1, 2, 99})]
        self.assertEqual(research.select(results, {1, 2, 3, 4}, {1, 2}, .25)[0], 'conservative')
        self.assertEqual(research.select(results, {1, 2, 3, 4}, {1, 2, 99}, .25)[0], 'conservative')
        self.assertEqual(research.select(results, {1, 2, 3, 4}, {1, 2}, 0)[0], 'abstain')

    def test_feature_and_reconstruction_safety_controls(self):
        research.audit.self_check()

    def test_prior_feedback_controls_preserve_both_pierron_cases_and_reject_small_terminal_loss(self):
        sectors = {('113', '15'): {3}, ('123', '15'): {1}, ('143', '28'): set()}
        self.assertTrue(research.passes_development_controls(sectors))
        self.assertFalse(research.passes_development_controls(sectors | {('123', '15'): set()}))
        self.assertFalse(research.passes_development_controls(sectors | {('143', '28'): {4}}))


if __name__ == '__main__':
    unittest.main()
