#!/usr/bin/env python3
import json
import sys

sys.stdin.read()
events = [
    {"type": "thread.started", "thread_id": "fixture"},
    {"type": "item.completed", "item": {"type": "command_execution", "command": "rg fixture"}},
    {"type": "item.completed", "item": {"type": "file_change", "changes": []}},
    {"type": "item.completed", "item": {"type": "agent_message", "text": "{}"}},
    {"type": "turn.completed", "usage": {"input_tokens": 10, "cached_input_tokens": 4, "output_tokens": 3, "reasoning_output_tokens": 2}},
]
for event in events:
    print(json.dumps(event, separators=(",", ":")), flush=True)
