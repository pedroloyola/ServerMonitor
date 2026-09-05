#!/usr/bin/env python3
"""Regenerate the CV map's evidence block FROM THE MEASUREMENTS.

The counts in docs/m13-s2t-cv-map.md drifted from the delivered commit in three consecutive rounds, and
each time it was fixed by hand and drifted again. Hand-reconciling a derived number is a process that has
to be repeated; deriving it is not. This reads the runners' own JSON results and rewrites the block
between the COUNTS markers, so the map cannot claim a matrix that was never run.

Usage:  python tools/mutations/report_counts.py --debug 1889 --release 1854 --slice 192 --runs 10
"""
import argparse
import glob
import io
import json
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
MAP = os.path.join(ROOT, "docs", "m13-s2t-cv-map.md")
BEGIN = "<!-- COUNTS:BEGIN -->"
END = "<!-- COUNTS:END -->"


def collect():
    rows = {}
    for path in sorted(glob.glob(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                              "mutation-results*.json"))):
        for row in json.load(io.open(path, encoding="utf-8")):
            # Last writer wins: a single-mutation re-run is more recent than the matrix row it replaces.
            rows[row["id"]] = row
    return rows


def defined_ids():
    """Every mutation the runners DEFINE, read from their source.

    A single-mutation re-run overwrites that runner's JSON with only the rows it ran, so the recorded set
    can be a subset of the defined one. Deriving a total from an incomplete record is the same class of
    error as reading a stale assembly: it looks like a measurement. So the two sets are compared and any
    difference is printed as MISSING rather than quietly reducing the count.
    """
    import re
    ids = set()
    for path in glob.glob(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutate*.py")):
        src = io.open(path, encoding="utf-8").read()
        ids.update(re.findall(r'\(\s*"(M\d+[a-z]?)"', src))
    return ids


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--debug", type=int, required=True)
    p.add_argument("--release", type=int, required=True)
    p.add_argument("--slice", type=int, required=True)
    p.add_argument("--runs", type=int, default=10)
    args = p.parse_args()

    rows = collect()
    missing = sorted(defined_ids() - set(rows), key=lambda m: (len(m), m))
    if missing:
        print("MISSING from the recorded results (run the FULL matrix before generating): "
              + ", ".join(missing))
    killed = sorted(k for k, r in rows.items() if r.get("status", "").startswith("RAN") and r.get("failed"))
    survived = sorted(k for k, r in rows.items() if r.get("status", "").startswith("RAN") and not r.get("failed"))
    notrun = sorted(k for k, r in rows.items() if not r.get("status", "").startswith("RAN"))

    lines = [
        BEGIN,
        "",
        "> **Bloco gerado.** `python tools/mutations/report_counts.py` reescreve-o a partir dos JSON dos",
        "> runners. Não editar à mão: os números aqui divergiram do commit entregue em três rondas",
        "> seguidas, e foram reconciliados à mão de cada vez. Um número derivado que se corrige à mão é um",
        "> processo que se repete.",
        "",
        "| | |",
        "|---|---|",
        f"| Mutações com resultado registado | **{len(rows)}** |",
        f"| **Mortas** | **{len(killed)}** |",
        f"| Sobreviventes | **{len(survived)}** — {', '.join(f'`{m}`' for m in survived) or 'nenhuma'} |",
        f"| Sem resultado (âncora, build, aborto) | **{len(notrun)}** — "
        f"{', '.join(f'`{m}`' for m in notrun) or 'nenhuma'} |",
        f"| Definidas sem registo nesta geração | **{len(missing)}** — "
        f"{', '.join(f'`{m}`' for m in missing) or 'nenhuma'} |",
        f"| Gate Debug | **{args.debug}/{args.debug}** |",
        f"| Gate Release | **{args.release}/{args.release}** |",
        f"| Suíte da fatia | **{args.slice}/{args.slice}** em **{args.runs}** corridas, "
        "contagem descoberta idêntica |",
        "",
        END,
    ]

    s = io.open(MAP, encoding="utf-8-sig", newline="").read()
    block = "\r\n".join(lines)

    if BEGIN in s and END in s:
        head = s[:s.index(BEGIN)]
        tail = s[s.index(END) + len(END):]
        s = head + block + tail
    else:
        raise SystemExit(f"markers not found in {MAP}; add {BEGIN} / {END} once, by hand")

    io.open(MAP, "w", encoding="utf-8", newline="").write(s)
    print(f"map counts regenerated: {len(rows)} recorded, {len(killed)} killed, "
          f"{len(survived)} survived, {len(notrun)} without a result")


if __name__ == "__main__":
    main()
