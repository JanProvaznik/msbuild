# Final focused review results

SDK: **10.0.400**, source commit
`14fbf8d5271c98133561eb55185fdb05b286f578`.

Native flags:

```text
NativeCoreBuild=true
NativeCoreBuildContract=DeclaredInputsAndHooksV1
```

Per-project activation probes observed a nonempty `_NativeCompletion` and the
acknowledged contract. The original baseline reproduced seven MAJOR defects.
The candidate fixes five concrete standard-input defects; two architectural
incompatibilities remain.

## Fixed for the executable counterexamples

| ID | Original severity | Verified current result |
|---|---|---|
| 3 | MAJOR | Opaque binary-reference fallback runs RAR; Lib.dll remains unchanged while updated Dep.dll is copied; native and stock print `new`. |
| 4 | MAJOR | Changing ServerGarbageCollection writes `System.GC.Server=true` in both runtimeconfig outputs. |
| 5 | MAJOR | Execution-time mapping change copies package-two text; native core remains skipped while the translation leaf performs copying. |
| 6 | MAJOR | Changing Company causes one native Csc invocation and the compiled attribute becomes `NewCompany`, matching stock. |
| 7 | MAJOR | Changing Compile CopyToOutputDirectory to PreserveNewest creates the requested Program.cs output in both native and stock; the public copy query still returns the item. |

All native and stock builds for these five cases exited zero. Warm core payloads
still skipped where applicable, so this result is not based solely on disabling
native incrementality.

## Remaining architectural incompatibilities

| ID | Current severity | Verified result |
|---|---|---|
| 1 | MAJOR | Native builds a referenced project before the parent's PreBuildEvent hook generates its source, failing CS2001; stock runs the generator first and succeeds. |
| 2 | MAJOR | Warm native Build skips CoreCompile validation and state-producing hooks; a surviving Build observer receives empty SDK/custom state. Stock runs the validation hook. |

Cases 1/2 reproduced with ACK in the preceding candidate run. Its three native
target hashes are identical to the final standard-input run's hashes. Neither
the contract acknowledgment nor additional signature entries repair these
architectural differences.

## Scope of the conclusion

- Accept the five fixes for these bounded regression cases.
- Keep coarse CoreBuild as explicit **lab/research evidence**, rejected for
  general rollout or claims of compatibility with arbitrary target hooks.
- Do not claim complete coverage for arbitrary generated assembly-attribute
  hooks, source-control state, reference discovery environments, or all SDK
  properties.
- Additional before/after-hook authoring and source-control architecture were
  not certified or fixed by this pass.
- This bundle does not independently certify or benchmark the separate SAFE SDK
  leaf patches. No full workloads were run.

The runner and source templates are portable. It requires explicit external
artifact and native-target directories, refuses to overwrite evidence, and
fails closed if the native boundary is not activated. Compact recorded outputs
and exact target hashes are in `evidence/`.
