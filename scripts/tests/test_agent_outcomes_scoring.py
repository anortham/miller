import sys
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))

from benchlib.agent_outcomes_scoring import (
    amortized_setup_costs,
    cost_per_success,
    paired_cluster_confidence_interval,
    summarize_attempts,
)


class AgentOutcomesScoringTests(unittest.TestCase):
    def test_cost_per_success_includes_failed_attempts(self):
        rows = [{"correct": True, "cost": 2.0}, {"correct": False, "cost": 8.0}]
        self.assertEqual(10.0, cost_per_success(rows))
        self.assertIsNone(cost_per_success([{"correct": False, "cost": 8.0}]))
        self.assertEqual(0.0, cost_per_success([{"correct": True, "cost": 0.0}]))

    def test_missing_failed_attempt_cost_makes_total_unknown(self):
        rows = [{"correct": True, "cost": 2.0}, {"correct": False, "cost": None}]
        self.assertIsNone(cost_per_success(rows))

    def test_missing_setup_cost_makes_full_total_unknown_but_preserves_subtotal(self):
        rows = [self.row("correct", 2.0, 10, 5.0)]
        setup = [
            {
                "environment_id": "env",
                "component_id": "restore",
                "cost": None,
                "wall_time_seconds": 4.0,
            }
        ]

        result = summarize_attempts(rows, setup)

        self.assertIsNone(result["total_cost"])
        self.assertEqual(2.0, result["measured_cost_subtotal"])
        self.assertEqual({"measured": 1, "total": 2}, result["cost_coverage"])

    def test_cached_tokens_are_not_double_counted_and_use_the_cached_price(self):
        row = {
            "total_model_input_tokens": 100,
            "total_model_cached_tokens": 40,
            "total_model_output_tokens": 10,
        }
        pricing = {
            "input_per_million": 2.0,
            "cached_input_per_million": 0.5,
            "output_per_million": 8.0,
        }

        from benchlib.agent_outcomes_scoring import model_token_total, price_model_usage

        self.assertEqual(110, model_token_total(row))
        self.assertEqual(
            (60 * 2.0 + 40 * 0.5 + 10 * 8.0) / 1_000_000,
            price_model_usage(row, pricing),
        )
        with self.assertRaisesRegex(ValueError, "cached input"):
            model_token_total({**row, "total_model_cached_tokens": 101})
        self.assertIsNone(
            model_token_total(
                {
                    "total_model_input_tokens": None,
                    "total_model_cached_tokens": None,
                    "total_model_output_tokens": None,
                }
            )
        )
        with self.assertRaisesRegex(ValueError, "all measured"):
            model_token_total({**row, "total_model_cached_tokens": None})

    def test_summary_excludes_voids_only_from_correctness_denominator_and_keeps_spend(
        self,
    ):
        rows = [
            self.row("correct", 2.0, 10, 5.0),
            self.row("incorrect", 3.0, 20, 7.0),
            self.row("infrastructure_void", 5.0, 30, 11.0),
            self.row("timeout", None, None, 13.0),
        ]

        result = summarize_attempts(rows)

        self.assertEqual(1 / 3, result["success_rate"])
        self.assertEqual(1 / 3, result["wrong_action_rate"])
        self.assertEqual(1 / 3, result["timeout_product_error_rate"])
        self.assertEqual(4, result["attempt_count"])
        self.assertEqual(1, result["infrastructure_void_count"])
        self.assertEqual(5.0, result["infrastructure_void_spend"])
        self.assertIsNone(result["total_cost"])
        self.assertEqual(10.0, result["measured_cost_subtotal"])
        self.assertEqual({"measured": 3, "total": 4}, result["cost_coverage"])
        self.assertIsNone(result["total_model_tokens"])
        self.assertEqual({"measured": 3, "total": 4}, result["token_coverage"])
        self.assertEqual(36.0, result["total_wall_time_seconds"])

    def test_setup_cost_is_once_then_amortized_without_double_counting_shared_broker(
        self,
    ):
        components = [
            {
                "environment_id": "semantic-a",
                "component_id": "model",
                "cost": 9.0,
                "wall_time_seconds": 30.0,
            },
            {
                "environment_id": "shared-broker",
                "component_id": "broker",
                "cost": 1.0,
                "wall_time_seconds": 2.0,
            },
            {
                "environment_id": "shared-broker",
                "component_id": "broker",
                "cost": 1.0,
                "wall_time_seconds": 2.0,
            },
        ]

        result = amortized_setup_costs(components)

        self.assertEqual(10.0, result["cold_setup_cost"])
        self.assertEqual({"1": 10.0, "10": 1.0, "100": 0.1}, result["cost_per_task"])
        self.assertEqual(32.0, result["cold_setup_wall_time_seconds"])

    def test_cluster_interval_keeps_repetitions_nested_and_is_deterministic(self):
        rows = []
        for repository, task, delta in (
            ("repo-a", "task-a", 1),
            ("repo-a", "task-b", 1),
            ("repo-b", "task-c", -1),
        ):
            for repetition in range(1, 6):
                rows.append(
                    {
                        "repo_id": repository,
                        "task_id": task,
                        "repetition": repetition,
                        "paired_delta": delta,
                    }
                )

        first = paired_cluster_confidence_interval(rows, samples=400, seed=17)
        second = paired_cluster_confidence_interval(rows, samples=400, seed=17)

        self.assertEqual(first, second)
        self.assertEqual(3, first["task_cluster_count"])
        self.assertEqual(2, first["repository_cluster_count"])
        self.assertEqual(15, first["observation_count"])
        self.assertEqual("inconclusive", first["conclusion"])

    @staticmethod
    def row(outcome, cost, tokens, wall):
        return {
            "outcome": outcome,
            "correct": outcome == "correct",
            "cost": cost,
            "tokens": tokens,
            "wall_time_seconds": wall,
        }


if __name__ == "__main__":
    unittest.main()
