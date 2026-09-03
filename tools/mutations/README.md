# M13 S2-T — mutation runners

These are the scripts `docs/m13-s2t-cv-map.md` cites. They lived in a scratch directory and the map
pointed at files nobody could open, which is CV-15 inverted: a document asserting evidence that cannot be
reached is not evidence. They are committed so a reviewer can reproduce any row of the matrix.

## Running one

```
python tools/mutations/mutate.py M1          # or any subset of M1..M18
python tools/mutations/mutate_t14.py M19     # M19..M25
python tools/mutations/mutate_wiring.py M26  # M26..M35
python tools/mutations/mutate_notice.py M36  # M36..M40
python tools/mutations/mutate_round9.py M41  # M41..M45
python tools/mutations/mutate_round10.py M46 # M46..M52
python tools/mutations/cs8509_differential.py
```

Each run applies ONE mutation to production code, executes the filtered suite, restores the file, and
prints the failure count with the names of the tests that failed. Nothing is left modified: the restore
is in the same iteration, and a run that finds its anchor missing restores and reports `ANCHOR NOT FOUND`
rather than guessing.

## Two things that will waste your time if you do not know them

* **Never wrap `dotnet test` in an external `timeout`.** Killing the parent orphans the test host, the
  orphan keeps a lock on `ServerMonitor.App.Tests.dll`, the next build fails to copy it with `MSB3021`,
  and every subsequent run silently executes the STALE assembly. Use `--blame-hang-timeout` instead.
* **A green `Passed!` line does not mean the run completed.** An aborted run prints both
  `Test host process crashed` and a `Passed!` line for whatever finished first. Grep for `Aborted` and
  for build errors as well as for the count.
