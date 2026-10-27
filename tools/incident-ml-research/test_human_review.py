import unittest
from human_review import normalize_decision, sector_numbers, evidence_flags


class HumanReviewTests(unittest.TestCase):
    def test_blank_and_uncertain_decisions_are_never_negative_labels(self):
        self.assertEqual(normalize_decision('Nie'), 'no')
        self.assertEqual(normalize_decision('TAK'), 'yes')
        self.assertEqual(normalize_decision('Nie wiem'), 'unknown')
        self.assertEqual(normalize_decision(''), 'unknown')
        with self.assertRaises(ValueError):
            normalize_decision('chyba')

    def test_multiple_sector_labels_are_preserved_without_guessing_from_notes(self):
        self.assertEqual(sector_numbers('1 oraz 4'), [1, 4])
        self.assertEqual(sector_numbers(3.0), [3])
        self.assertEqual(sector_numbers('Nie wiem'), [])
        with self.assertRaises(ValueError):
            sector_numbers('2026')

    def test_outcome_only_reasons_are_not_independent_incident_evidence(self):
        self.assertIn('outcome_only', evidence_flags('yes', [], 'dnf'))
        self.assertIn('outcome_only', evidence_flags('no', [], 'przecież wygrał wyścig'))
        self.assertNotIn('outcome_only', evidence_flags('yes', [3], 'dnf po upadku w S3'))
        self.assertNotIn('outcome_only', evidence_flags('no', [], 'potwierdziłem czysty przejazd'))


if __name__ == '__main__':
    unittest.main()
