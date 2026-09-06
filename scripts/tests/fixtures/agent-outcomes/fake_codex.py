#!/usr/bin/env python3
import json
import os
import sys
from pathlib import Path

capture = os.environ.get("AGENT_OUTCOMES_FAKE_CAPTURE")
if capture:
    Path(capture).write_text(json.dumps({"argv": sys.argv[1:]}), encoding="utf-8")

events = [
    {"type": "thread.started", "thread_id": "fixture"},
    {"type": "item.completed", "item": {"type": "command_execution", "command": "python -m unittest"}},
    {"type": "item.completed", "item": {"type": "file_change", "changes": []}},
    {"type": "item.completed", "item": {"type": "agent_message", "text": "{}"}},
    {"type": "turn.completed", "usage": {"input_tokens": 10, "cached_input_tokens": 4, "output_tokens": 3, "reasoning_output_tokens": 2}},
]
for event in events:
    print(json.dumps(event, separators=(",", ":")), flush=True)
