#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from benchlib.agent_outcomes_controller import (
    execute_campaign,
    freeze_campaign,
    load_strict_json,
    public_frozen_campaign,
    score_run,
    validate_task_manifest,
)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Run the frozen agent-outcomes benchmark."
    )
    commands = parser.add_subparsers(dest="command", required=True)
    validate = commands.add_parser("validate")
    validate.add_argument("--tasks", required=True)
    freeze = commands.add_parser("freeze")
    freeze.add_argument("--config", required=True)
    freeze.add_argument("--output", required=True)
    run = commands.add_parser("run")
    run.add_argument("--campaign", required=True)
    run.add_argument("--dry-run", action="store_true")
    run.add_argument("--approval")
    run.add_argument("--output", required=True)
    score = commands.add_parser("score")
    score.add_argument("--run", required=True)
    score.add_argument("--output", required=True)
    args = parser.parse_args(argv)
    try:
        if args.command == "validate":
            result = validate_task_manifest(args.tasks)
        elif args.command == "freeze":
            result = public_frozen_campaign(freeze_campaign(args.config, args.output))
        elif args.command == "run":
            approval = None
            if args.approval:
                approval = load_strict_json(args.approval)
            result = execute_campaign(
                args.campaign, args.output, dry_run=args.dry_run, approval=approval
            )
        else:
            result = score_run(args.run, args.output)
    except (
        OSError,
        ValueError,
        PermissionError,
        RuntimeError,
        json.JSONDecodeError,
    ) as exc:
        print(str(exc), file=sys.stderr)
        return 2
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
