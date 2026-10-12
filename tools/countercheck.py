#!/usr/bin/env python3
"""Counter-check a fix: undo each part of it in turn and show which tests turn red.

A passing test only proves the fixed code passes it; this shows each part of the fix is guarded.
Give it a JSON file with a list of mutations, each {"name", "file", "old", "new"}: "old" must occur
in "file" exactly as written and is replaced by "new" for one test run, then the file is restored.

    python3 tools/countercheck.py mutations.json [--filter CorpusTests] [--framework net10.0]

Every mutation must end red; one that stays green means no test guards that part of the fix.
"""
import argparse, json, os, re, shutil, subprocess, sys

parser = argparse.ArgumentParser()
parser.add_argument("mutations")
parser.add_argument("--filter", default="FullyQualifiedName~CorpusTests")
parser.add_argument("--framework", default="net10.0")
parser.add_argument("--project", default="tests/NPlusOne.Analyzers.Tests")
args = parser.parse_args()

unguarded = 0
for m in json.load(open(args.mutations)):
    text = open(m["file"]).read()
    if text.count(m["old"]) != 1:
        sys.exit(f"{m['name']}: 'old' must occur exactly once in {m['file']}")
    shutil.copy(m["file"], m["file"] + ".bak")
    open(m["file"], "w").write(text.replace(m["old"], m["new"]))
    try:
        out = subprocess.run(["dotnet", "test", args.project, "-f", args.framework, "--filter", args.filter, "-nologo"],
                             capture_output=True, text=True).stdout
    finally:
        shutil.move(m["file"] + ".bak", m["file"])
        os.utime(m["file"], None)  # newer than the mutated build, so the next build compiles the restored file
    if "Failed!" in out:
        status = "red"
    elif "Passed!" in out:
        status = "GREEN: no test guards this part"
        unguarded += 1
    else:
        status = "did not build"
        unguarded += 1
    print(f"== {m['name']}: {status}")
    for line in sorted({re.sub(r"^\s+", "", l)[:100] for l in out.splitlines() if re.search(r"(missed|unexpected):\s", l)}):
        print("   ", line)

sys.exit(1 if unguarded else 0)
