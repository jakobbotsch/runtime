// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/*****************************************************************************/
#ifndef CompMemKindMacro
#error Define CompMemKindMacro before including this file.
#endif

// This list of macro invocations should be used to define the CompMemKind enumeration,
// and the corresponding array of string names for these enum members.

// clang-format off
CompMemKindMacro(ABI)
CompMemKindMacro(ASTNode)
CompMemKindMacro(ArrayStack)
CompMemKindMacro(AssertionProp)
CompMemKindMacro(BasicBlock)
CompMemKindMacro(CSE)
CompMemKindMacro(CallArgs)
CompMemKindMacro(ClassLayout)
CompMemKindMacro(Codegen)
CompMemKindMacro(CopyProp)
CompMemKindMacro(CorTailCallInfo)
CompMemKindMacro(DebugInfo)
CompMemKindMacro(DebugOnly)
CompMemKindMacro(DepthFirstSearch)
CompMemKindMacro(DominatorMemory)
CompMemKindMacro(EarlyProp)
CompMemKindMacro(FieldSeqStore)
CompMemKindMacro(FixedBitVect)
CompMemKindMacro(FlowEdge)
CompMemKindMacro(GC)
CompMemKindMacro(Generic)
CompMemKindMacro(ImpStack)
CompMemKindMacro(Inlining)
CompMemKindMacro(InstDesc)
CompMemKindMacro(Liveness)
CompMemKindMacro(LSRA)
CompMemKindMacro(LSRA_Interval)
CompMemKindMacro(LSRA_RefPosition)
CompMemKindMacro(LocalAddressVisitor)
CompMemKindMacro(LoopClone)
CompMemKindMacro(LoopHoist)
CompMemKindMacro(LoopIVOpts)
CompMemKindMacro(LoopOpt)
CompMemKindMacro(LoopUnroll)
CompMemKindMacro(Loops)
CompMemKindMacro(LvaTable)
CompMemKindMacro(MemoryPhiArg)
CompMemKindMacro(MemorySsaMap)
CompMemKindMacro(ObjectAllocator)
CompMemKindMacro(Pgo)
CompMemKindMacro(Promotion)
CompMemKindMacro(RangeCheck)
CompMemKindMacro(Reachability)
CompMemKindMacro(SSA)
CompMemKindMacro(SiScope)
CompMemKindMacro(SideEffects)
CompMemKindMacro(TailMergeThrows)
CompMemKindMacro(TreeStatementList)
CompMemKindMacro(Unknown)
CompMemKindMacro(UnwindInfo)
CompMemKindMacro(ValueNumber)
CompMemKindMacro(VariableLiveRanges)
CompMemKindMacro(ZeroInit)
CompMemKindMacro(bitset)
CompMemKindMacro(hashBv)
//clang-format on

#undef CompMemKindMacro
