#!/usr/bin/env python3
"""Validate repository doc links, sample model snippets and the documented sample output."""
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parent.parent
GITHUB = "https://github.com/Trexoncorp/Trexon.Data/blob/main/"


def main():
    for document in [*ROOT.glob("README*.md"), *ROOT.joinpath("docs").glob("*.md")]:
        for link in re.findall(r"\]\(([^)]+)\)", document.read_text()):
            if link.startswith(GITHUB):
                target = ROOT / link.removeprefix(GITHUB).split("#")[0]
            elif re.match(r"^[a-z]+:", link) or link.startswith("#"):
                continue
            else:
                target = document.parent / link.split("#")[0]
            if not target.exists():
                raise SystemExit(f"Broken documentation link: {document.relative_to(ROOT)} -> {link}")

    sample = ROOT.joinpath("examples/Trexon.Data.Sample/Program.cs").read_text()
    guide = ROOT.joinpath("docs/RELATIONSHIPS.md").read_text()
    for model in ("User", "UserProfile", "Post", "Role"):
        start = sample.index(f"public sealed class {model}\n")
        end = sample.index("\n}", start) + 2
        if sample[start:end] not in guide:
            raise SystemExit(f"The documented {model} model differs from the sample")
    expected = re.search(r"```text\n(.*?)\n```", guide, re.S)
    if expected is None:
        raise SystemExit("The relationships guide must contain the expected sample output")
    result = subprocess.run(
        ["dotnet", "run", "--project", "examples/Trexon.Data.Sample", "--configuration", "Release", "--no-build"],
        cwd=ROOT, check=True, capture_output=True, text=True,
    )
    if result.stdout.strip() != expected.group(1):
        raise SystemExit(f"The sample output differs from its documentation:\n{result.stdout}")
    print("Documentation links, sample models and sample output: OK")


if __name__ == "__main__":
    main()
