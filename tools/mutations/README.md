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

## A surviving mutation is not always a weak test

Round 6 had two mutations come back to life (`M49`, `M70`). Neither test had been weakened. A *second copy
of the same rule* had been added in front of the first, so each copy covered for the other and neither
mutation could change behaviour on its own.

The reflex is to re-form the mutation so it disables both copies. **Do not start there.** That restores the
green while leaving the duplication in the code — the evidence goes back to hiding the defect that produced
it. Ask first whether the two places state the *same* rule or two different ones:

- **Different rules** (ordering vs. eligibility, say): keep both, and the mutation must be re-formed,
  because each deserves its own proof.
- **The same rule**: delete a copy. One statement of the rule, one mutation against it.

M49 and M70 were retired, not re-formed. Their properties are attacked by `M78` and `M79` against the
single remaining statement — and that was **verified by looking at which tests kill them**: the killers are
literally the old property tests (`A_decision_taken_before_the_deadline_...`,
`The_deadline_is_re_read_at_the_invocation_...`, `T9_session_semantics_...`), not tests with similar names.

Retiring a mutation is only honest with that check. Write down which mutation inherits the property and
which test kills it, or the retirement is a deletion of evidence.

## Anchors break when the code legitimately changes shape

Round 6 turned a `bool` return into `void` and one `Invoke` into a loop, which broke four anchors (`M69`,
`M71`-`M73`). `ANCHOR NOT FOUND` is not a pass and not a failure — it is *no result*, and a matrix full of
them is a matrix that proves nothing. Re-anchor against the code as it stands and keep the property the
mutation was always attacking, even when the new form looks different: `M71` used to hand a permission
back; with a `void` commit it decides and then does not perform, which is the same defect.

## Rewriting a test to make it deterministic can make it WEAKER

This has now happened three times in this slice — M51 in round 4, M71/M73 in round 5, and M51 again in
round 7 — always the same way: a test is rewritten to remove a timing dependency, the suite stays green,
and a mutation that the test used to kill quietly comes back to life. Green proves the test still passes.
It proves nothing about whether the test can still FAIL.

Round 7's instance is the clearest. The DPI test proved exclusion by watching for
`ThreadState.WaitSleepJoin`. That was replaced with an arrival seam and an immediate negative assertion —
deterministic, no clock, and unable to fail: with the gate deleted the foreign call simply had not run
*yet* when the assertion executed, so `M51` (which deletes the gate) passed.

**So the delivery checklist is not "I ran the matrix". It is: for every test I rewrote, I re-ran the
mutations that test was killing, by name, and watched them die again.** Running the whole matrix catches
it too, but only if you read the rows for the tests you touched instead of the summary count.

The fix that worked, in Atlas's words: **a seam that identifies the MONITOR, not the THREAD.**
`ShellGateWaitersForTests` counts threads queued on `_nativeGate` itself. `ThreadState` says a thread is
parked somewhere and names nothing; a seam placed before the lock proves only that the thread reached that
statement. A waiter count on the specific monitor is positive, structural, and decides both directions:
with the gate present it reaches one and stays there, and with the gate deleted it can never reach one.

## Runners restore byte-exactly

Restoring a mutated file through a text write with `newline="\n"` rewrote CRLF sources as LF, so every run
left files modified in git with "LF will be replaced by CRLF". The measuring instrument was dirtying the
tree it measured. Originals are now kept as raw bytes and written back unchanged, and the mutated write
reuses whatever line ending the file already had.
