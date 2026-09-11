# Explicit code entries and runtime async resumption

> **Prototype status:** The call-based revision builds and passes the ordinary
> async suite, but is not validated for use: `DOTNET_GCStress=0xC` intermittently
> exposes invalid GC roots or continuation corruption with wrappers enabled.
> Keep the feature disabled by default. Successful non-stress runs do not
> establish correctness of the independent-frame GC/unwind implementation.

## Goal

A runtime async continuation should name a JIT-generated wrapper rather than an
EE-generated adapter. The wrapper calls a state-specific body-resume entry
without reentering the ordinary method entry or dispatching through a state
switch. After the body returns, the wrapper propagates its result.

The wrapper owns a small frame and reserves the caller-side storage expected by
the body, without initializing dummy original arguments. The body-resume entry
establishes the normal body frame, restores continuation state, and branches to
the instruction following the await. The shared body's original return ABI and
return paths are unchanged.

This document separates the proposed design from implementation milestones.
In particular, reporting a code entry does not by itself make that entry
callable, establish its frame, or make register allocation multi-entry-correct.

## Entry kinds are not interchangeable

| Entry | Invocation | Frame relationship | Exit | Callable signature |
| --- | --- | --- | --- | --- |
| Primary method | Ordinary managed call | Establishes the method frame | Method return | Original method |
| Catch handler | Exception dispatch | Associated with an existing parent invocation | Returns a continuation address to the runtime | Catch/filter ABI |
| Filter | Exception dispatch during the first pass | Associated with an existing parent invocation | Returns a filter result | Catch/filter ABI |
| Finally or fault handler | Exception machinery or a finally call | Associated with an existing parent invocation | Returns to its invoker | Finally/fault ABI |
| Async wrapper | Continuation dispatch | Owns a small independent caller frame | Propagates the result and returns the next continuation | Async resume ABI |
| Body resumption | Call from the wrapper | Establishes a new main-compatible frame | Branches into the shared body, which returns to the wrapper | Body-resume ABI |

The logical successor of `BBJ_EHCATCHRET` is not an ordinary machine branch.
For example, on x64 the catch loads the continuation address into the return
register and executes its funclet epilog. The runtime then resumes execution.
Replacing it with `BBJ_ALWAYS` would bypass that protocol.

An async resumption entry has the opposite relationship: after frame setup and
state restoration, its transfer into the shared body really is an ordinary
branch. `BBJ_ALWAYS` is appropriate there. A new entry kind must not implicitly
acquire catch-parent-frame semantics merely because it is called a funclet.

## Wrapper IR and caller-side storage

The wrapper uses an ordinary `GT_CALL` with `CT_INDIRECT` and a symbolic
`GT_RESUME_ENTRY_ADDR` target. Lowering contains the target and codegen emits a
direct relative call. No target-address register, indirect-call dispatcher, or
fabricated EE method handle is needed.

Only real inputs are arguments: the continuation and a return-buffer pointer
when the original ABI requires one. Their ABI locations are taken from the
original method's parameter classification. Caller-side storage is a separate
reservation, not a list of dummy argument values. It generates no dummy pushes
or stores, and is folded into the wrapper's outgoing area where supported.

```text
wrapper entry arguments
value = CALL RESUME_ENTRY_ADDR(bodyEntry)(continuation, optional retbuf)
next = ASYNC_CONTINUATION
if next is null and resultStorage is not null:
    typed_store(resultStorage, value)
RETURN next
```

The continuation return register, ordinary value result, register kills, and
call-site GC state remain visible to LSRA and codegen. The call is not itself
transformed as another await. All state-specific wrappers share their result
propagation and return tail, with a common wrapper-frame layout.

## Suspension reachability and shared return tails

Unconditionally rooting every resumption entry prevents removal of entries whose
suspensions have disappeared. Instead, each state-specific suspension records a
logical resumption dependency:

```text
 suspension A                         suspension B
 save continuation A                 save continuation B
      |          :                        |          :
      |          : resumes at A           |          : resumes at B
      +------------------+----------------+
                         |
                  shared suspension tail
                  context restoration
                  GT_RETURN_SUSPEND
                  BBJ_RETURN

 wrapper A                           wrapper B
 call body entry A                   call body entry B
      |                                  |
      +---- shared result/return tail ---+

 body entry A                        body entry B
 body prolog                         body prolog
 restore A                           restore B
 BBJ_ALWAYS                          BBJ_ALWAYS
      |                                  |
      +---------- shared method body ----+
```

Solid edges describe execution within one invocation. Dotted edges are logical
dependencies across invocations. A conceptual `BBJ_SUSPEND` has one executable
successor (the suspension tail) and one resumption successor. It need not itself
emit a return.

The dependency belongs before the merge into shared suspension code. Attaching
all resume targets to the shared `BBJ_RETURN` would retain every entry whenever
any suspension remains reachable. Keeping the state-specific dependency permits
shared context-restoration code and shared epilogs, including the sharing needed
to respect the x86 epilog-count limit.

Reachability traverses both edge kinds. Removing the last reachable producer of
a continuation can therefore remove its resumption entry. Disconnected cycles
of suspensions and resumptions are also removable. A surviving suspension in a
resumed invocation can keep another entry reachable.

All code that publishes an entry address must participate in this dependency
model. The entry cannot be deleted while any reachable publisher remains.
Block splitting, merging, cloning, and continuation-state reuse must preserve
the association. Logical dependency edges must not be redirected as if they
were interchangeable ordinary branches.

## Register allocation

The async transformation runs after rationalization, avoiding a requirement to
rebuild early SSA and value numbering for multiple entries. That does not avoid
the late dataflow work: LSRA recomputes DFS and loop information, and later
cleanup still removes unreachable blocks.

The suspension dependencies and ordinary body edges require different treatment:

| Operation | Suspension to shared tail | Suspension to resumption | Resumption to body |
| --- | --- | --- | --- |
| Reachability | Follow | Follow | Follow |
| Register-location propagation | Ordinary | None | Ordinary |
| Edge resolution moves | Allowed | Forbidden | Allowed |
| Incoming local values | Current invocation | Explicit entry definitions | Restored values |

The wrapper's incoming continuation and result-storage pointer are definitions
with fixed ABI locations. The internal body call passes only its actual inputs;
the body entry defines them in their original ABI locations. Loads from the
continuation define the restored locals.
Ordinary body locals must not be implicitly live into the external entry.
The entry must not borrow register locations from whichever block happened to
be allocated previously.

After restoration, the ordinary edge into the body lets LSRA reconcile locations
with the synchronous path. There is no requirement to spill all restored locals.
Likewise, nothing about the logical resumption dependency should force ordinary
register values to be spilled before the shared suspension tail.

The wrapper-to-body call is a real call boundary, not register-location
propagation between basic blocks. Wrapper locals live across the call must be
preserved in the wrapper's frame or nonvolatile registers. Normal control flow
after the call leads to result propagation; body returns are not ordinary CFG
edges back to every possible caller.

Existing EH restrictions remain for locals actually exposed to EH flow.
Reusing `hasEHBoundaryIn` or `hasEHBoundaryOut` indiscriminately would import
incorrect stack-home and write-through assumptions. In particular, a suspension
block has an ordinary outgoing edge as well as an invocation boundary.

DFS order across the logical dependency is useful for reachability but does not
prove dominance or machine-state availability within a new invocation.
Consumers of those properties need an appropriate graph view or explicit
entry-boundary semantics.

## Native frame and runtime contract

Restoring state means more than restoring managed local values. At the branch
into the body, the following must agree with the primary invocation:

- Stack and frame pointers, alignment, outgoing argument space, and spill slots.
- Callee-preserved register saves and the return-address location.
- Any required zero initialization, security cookie, and frame metadata.
- GC liveness, including the incoming continuation during restoration.
- Exception and unwind interpretation of both the entry and the shared body.

The main body's epilog returns to the wrapper's call site, not the dispatcher.
There must be no hidden clobber or frame transition after LSRA inserts resolution
moves at the end of restoration.

The return ABI also needs adaptation. The resume signature is conceptually
`Continuation Resume(Continuation continuation, ref byte resultStorage)`.
It is not the runtime-async ABI of the original method. For example, on Windows
x64 the original async call returns its continuation in RCX, while the resume
function's ordinary return value belongs in RAX.

On successful resumed completion, the result must be written to the incoming
result-storage address and the resume invocation must return a null
continuation. On another suspension it must return the new continuation using
the resume ABI. Normal callers must retain the original result and continuation
conventions. The wrapper owns this adaptation; the main body needs no extra
return branches or duplicate return paths. A discarded result can have a null
destination, while a retbuf call still requires valid temporary result storage.

Restoration is a GC-noninterruptible region. Fully overwritten slots need not
first be zeroed. The required initialization set is the GC-visible storage not
fully initialized by restoration, plus live default values intentionally omitted
from the continuation. In particular, the body cannot trust unspecified
caller-allocated GC argument homes to contain valid roots.

Two frames may have the same method identity, just as in recursion. They must
nevertheless have separate frame/root descriptions. While the body runs, the
wrapper is an inactive caller and must report its own live result destination
and result-buffer roots, not the body's untracked locals.

The runtime must not search for an EH parent frame for an async entry. An
additional native unwind record alone does not communicate that distinction.
Tiering and OSR also need explicit rules: a continuation must continue naming
code whose lifetime and frame layout are valid, rather than accidentally
dispatching through a different version's state numbering.

## Explicit JIT-EE entry reporting

The existing unwind API reports code ranges and unwind bytes, but its
`CorJitFuncKind` classification only names root, handler, and filter code.
Some consumers infer that any secondary range is an EH funclet. The AOT
interface also maps that enum into serialized frame flags.

The proposed reporting contract declares entries independently:

```cpp
void reportCodeEntry(
    uint32_t startOffset,
    uint32_t endOffset,
    CorInfoCodeEntryKind kind,
    CorInfoCodeEntrySignature signature,
    uint32_t gcInfoOffset);
```

The owning method and code version are implicit in the compilation. Entry kind
describes runtime semantics; signature kind selects a known callable ABI. The signature choices
include the original method, catch/filter, finally/fault, external async wrapper,
and internal body-resume conventions.
This avoids requiring the JIT to manufacture an arbitrary `CORINFO_SIG_INFO`.

An entry declaration is not an EH clause. It is also not necessarily one-to-one
with unwind records: split functions may require several unwind regions.
Unwind allocation and EH reporting retain their existing purposes.

GC information is allocated once as a combined buffer. The ordinary method and
body-resume entries use blob offset zero. Wrapper entries select an independent
wrapper blob using `gcInfoOffset`. Every blob uses method-global native offsets,
so selecting the appropriate blob does not rebase the decoder's instruction
offset. Wrapper maps have their own slots and frame properties and do not inherit
the body's generic-context, vararg, or untracked-local metadata. GC-stress
instrumentation must visit each distinct map.

The prototype contract uses half-open offsets relative to the hot code buffer.
A production generalization must explicitly account for cold code and any
target-specific address spaces rather than guessing a buffer from an offset.

The VM prototype allocates the entry table lazily from the method's JIT metadata
heap, retaining the method's collectible or dynamic-method lifetime. It adds a
table pointer to `RealCodeHeader`, which costs one pointer per native method even
when reporting is disabled. Eliminating or justifying that fixed overhead is
separate from avoiding allocation of unused entry tables.

Native consumers must preserve the new classification for code lookup, stack
walking, GC, diagnostics, and exception handling. Merely accepting the callback
without changing secondary-entry classification is insufficient.
SuperPMI must record the report as compilation output, and both managed JIT
interface implementations must consume it consistently.

`CORINFO_AsyncResumeInfo.Resume` already represents a callable continuation
target. Declaring the entry and publishing that pointer are separate operations.
The declaration need not describe continuation fields: restoration remains
JIT-generated code.

## WebAssembly

Wasm has independently useful function declarations even without native unwind
tables. The JIT currently reports per-function ranges through its unwind path,
including frame size and virtual-IP length. The ReadyToRun object-emission path
separately reconstructs funclet kinds from EH clauses to choose Wasm signatures
and declare function symbols.

Explicit entry reports can replace that EH-derived enumeration. The object
writer can select the Wasm signature from the signature-kind enum and emit an
entry even when no EH clause describes it. This matches the more general
function-body and symbol machinery already used by Wasm adapter and thunk nodes.
It does not require treating every auxiliary function as an EH handler.

Wasm still needs its own resumption implementation: a native branch between
code ranges is not a Wasm branch between independently declared functions.
Its structured control flow, shadow-stack state, virtual-IP mapping, and
dispatch mechanisms remain target-specific. Generalized reporting is shared
infrastructure, not a claim that the native implementation can be reused
unchanged.

## Implementation plan

1. Add entry and signature enums, implement reporting through the VM, managed
   interface, generated thunks, and SuperPMI, and preserve explicit entry kinds
   independently of EH metadata.
2. Introduce opt-in Windows x64 resumption entries for supported optimized JIT
   compilations. Give entries explicit extents, specialized prologs, and ABI
   input definitions. Publish their addresses through continuation resume info.
3. Connect state-specific suspensions to their entries while preserving shared
   suspension tails. Teach graph rewriting, liveness, and LSRA about the
   invocation boundary; retain ordinary register flow into the body.
4. Exercise multiple states, synchronous completion, repeated suspension,
   scalar and GC-containing returns, register pressure, GC, and EH. Verify
   generated entries and dispatch removal, not just functional test outcomes.
5. Extend eligibility to OSR, additional architectures, and AOT only after their
   frame, versioning, metadata, and code-lifetime contracts are implemented.
   Replace Wasm's EH-derived function declarations with explicit reporting.

The feature must remain disabled by default until the implementation and
validation cover those contracts. Unsupported compilation modes retain the
existing resumption mechanism through an explicit eligibility decision.

## Prototype implementation

Set `DOTNET_JitAsyncResumeEntries=1` to enable the prototype. It is off by
default. The current implementation targets optimized Windows x64 JIT
compilations, not Tier0, OSR, AOT, or Wasm.

The existing legacy resumption adapter is already tied to a specific code
version through its final-code-address slot; it does not simply retarget an
outstanding continuation to the latest method entry. Direct entry pointers must
preserve that version association and lifetime. OSR remains excluded because its
frame reconstruction requires a separate handoff through the Tier0 version.

Eligible methods may have the single compiler-generated async context-restoring
fault handler. The call-based revision removes the previous blanket exclusions
for scalar stack arguments, large locals, and struct/return-buffer results.
Temporary implementation guards remain for user/nested EH, original
implicit-byref parameter pointee homes, reported generic context, unmanaged
calls, and frame instrumentation not yet covered by the prototype. These are
implementation gaps, not fundamental method-eligibility rules.

The prototype implements the logical edge as a `bbAsyncResume` dependency on the
state-specific suspension block, rather than adding `BBJ_SUSPEND`. The executable
successor and shared `BBJ_RETURN` tails remain unchanged. Full successor
enumeration includes the dependency; ordinary predecessor lists and register
resolution do not. A second dependency keeps the internal body entry reachable
from its wrapper call. Splitting blocks transfers the dependency with the
publishing operation or call.
Late compaction and layout optimizations that are not yet dependency-aware are
disabled for opted-in methods. This is an integration limitation, not a claim
that all existing graph optimizations understand the new edge.

Wrapper arguments use `GT_ASYNC_RESUME_ARG`; body-entry inputs use
`GT_RESUME_BODY_ARG` with the original parameter's ABI location. Both the
prolog's protected argument mask and GC reporting describe the surviving
definitions rather than unconditionally keeping unused inputs alive.

After LSRA, surviving wrappers receive `FUNC_ASYNC_WRAPPER` descriptors and body
entries receive `FUNC_ASYNC_RESUME` descriptors. Body entries reuse main-frame
setup without ordinary argument homing; restoration ends in `BBJ_ALWAYS`.
Wrappers have a separate frame layout and share their result propagation and
return tail.

The body's ordinary returns and suspension returns are unchanged. The wrapper
reads the async continuation result immediately after its call, copies a
successful result only when the destination is non-null, and returns the
continuation using its own ordinary pointer-return ABI.

Only opted-in, eligible methods issue entry reports. Their reports describe the
main body, the synthetic handler when present, wrappers, and body-resume entries.
The VM persists their entry kinds and GC-info offsets, treating the wrapper and
body as separate non-EH frames.
Unreported methods retain legacy classification. SuperPMI records and replays
the new callback, and the managed JIT interfaces retain the entry descriptors.
Wasm signature emission and the AOT code-manager formats have not been migrated
to the new representation.

The remaining work includes broader ABI/EH support, dependency-aware late
optimization and dedicated dead-producer removal coverage, production metadata
space accounting, OSR/versioning, and the target-specific Wasm implementation.

### Running the prototype

From a Windows x64 checkout:

```powershell
.\build.cmd clr+libs -rc Checked -lc Release
.\src\tests\build.cmd checked x64 -Tree async
.\src\tests\build.cmd checked x64 -GenerateLayoutOnly
$env:CORE_ROOT = "$PWD\artifacts\tests\coreclr\windows.x64.Checked\Tests\Core_Root"
$env:DOTNET_TieredCompilation = '0'
$env:DOTNET_JitAsyncResumeEntries = '1'
Push-Location artifacts\tests\coreclr\windows.x64.Checked\async\async
& "$env:CORE_ROOT\corerun.exe" .\async.dll Resumption
Pop-Location
```

Repeat with the flag set to `0`. Omit the `Resumption` filter to run the complete
async runner, including its out-of-process tests. Keep `CORE_ROOT` set and run
from the runner's directory so those child wrappers resolve their paths.
Check recorded test failures/results, not just the process exit code.

For entry-selection evidence, set `DOTNET_JitDump=ResumeAndJoin` on a Checked
build and run the `ResumptionJoinsSynchronousFlow` filter. Look for
`Async resume entries enabled`, an internal `CALL` whose target is
`RESUME_ENTRY_ADDR`, and the wrapper/body argument definitions. Verify stack
reservations do not contain stores of dummy original arguments.

## Validation criteria

Correctness tests must run with the prototype enabled and disabled, using a
refreshed Core_Root after rebuilding the product. A passing test is not evidence
of the prototype if the method was inlined, compiled in an unsupported mode, or
silently took the old path.

Inspect JIT dumps and disassembly to establish entry selection, the absence of
state dispatch on the selected path, correct main-frame setup, and register
propagation out of restoration. Exercise collection and exceptions while
running resumed code. Check that shared suspension tails and epilog counts
remain shared, and that removing a producer does not retain a dead entry.

Measure total generated code, including added entry prologs, as well as
resumption throughput. Eliminating a dispatch switch is not by itself evidence
of a net size or throughput improvement.

The earlier jump-only prototype passed the 149-case async runner with the flag off and on
with tiering disabled, and with the flag on and tiering enabled. The 17-case
resumption filter also passed LSRA stress modes `0x100` and `0x203`; the two
reference-preservation cases passed GC stress `0xC`. JIT dumps confirmed direct
entry selection for scalar, reference-preservation, and task-completion paths.
Two captured compilation contexts replayed successfully through SuperPMI.
These checks do not imply that every suite case used the new entry mechanism;
unsupported methods intentionally use the fallback. They are not validation of
the call-based revision, which must repeat these checks and the new stack-area
and GC-containing return-buffer cases.

## Earlier jump-only measurements

Before the call-based revision, a BenchmarkDotNet 0.16.0-preview.1 run on Windows x64, Ryzen 9 5950X,
used a Release runtime, disabled tiering, two launches, five warmup iterations,
and ten measurement iterations per launch. The benchmark calls a non-inlined
runtime async method that awaits `Task.Yield()` and returns its integer argument
plus one.

| Configuration | Mean | 99.9% confidence interval half-width | Compiled method bytes |
| --- | ---: | ---: | ---: |
| Prototype flag off | 513.7 ns | 10.75 ns | 326 |
| Prototype flag on | 489.8 ns | 11.79 ns | 395 |

The final run has a roughly 4.7% lower mean with the prototype enabled. However,
an earlier comparison measured 435.3 ns off versus 452.4 ns on, with overlapping
confidence intervals. This run-to-run variability and the narrow workload do not
establish a general throughput improvement.

The new entry and return-adapter code increased this method's size by 69 bytes
in both comparisons. The byte counts include entries emitted with the
method, but exclude the separately compiled legacy resumption adapter; they
are not a total method-plus-adapter size comparison.

Both measurements use the modified runtime, changing only the prototype flag.
They do not measure the unconditional metadata/header cost against an unmodified
upstream build. Broader measurements and code-size work are required before
enabling this design by default.
