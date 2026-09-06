#!/usr/bin/env python3
import sys

if len(sys.argv) > 2 and sys.argv[1:3] == ["container", "exists"]:
    if "notfound" in __file__:
        raise SystemExit(1)
    if "error" in __file__:
        raise SystemExit(125)
    raise SystemExit(0)
raise SystemExit(0)
