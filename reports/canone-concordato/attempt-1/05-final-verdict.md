# Final verdict — canone concordato attempt 1

STATO: GOAL_RAGGIUNTO

All audit discrepancies D-AC8, D-AC9, D-AC10, D-AC12, D-AC13 are `STATO: APPROVED`. AC1–AC7/AC11 remained PASS from the original audit.

## Re-run of 01-test-plan

```
dotnet test Casazen.Tests/Casazen.Tests.csproj --filter "FullyQualifiedName~CanoneConcordato|FullyQualifiedName~ComuneImu" -p:BaseOutputPath=artifacts\cc-loop\
```

`Passed! Failed: 0, Passed: 28, Skipped: 0`

| Id | Result |
|---|---|
| S-AC1–S-AC7, S-AC11 | PASS (unchanged L1) |
| S-AC8 | PASS — export route + DI + `%PDF` tests |
| S-AC9 | PASS — mark-sent route + event tests |
| S-AC10 | PASS — `lease.read` / `lease.register`; no `property.read` |
| S-AC12 | PASS (L2) — calculator, guidance, IMU button gated on `Registered` |
| S-AC13 | PASS (L2) — Italian keys in `it.json`; blob fetch via authenticated client |

L3 browser (Auth0 login on `:5173`) was not executed in this session. The test plan pass gate without a token is L1 + L2 file/unit asserts. No open `BLOCKED` discrepancy ids remain in the fix list.
