// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Internal.IL;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Microsoft.Diagnostics.Tools.Pgo
{
    internal class SampleProfile
    {
        public SampleProfile(
            MethodIL methodIL,
            FlowGraph fg,
            Dictionary<BasicBlock, long> samples,
            Dictionary<BasicBlock, long> smoothedSamples,
            Dictionary<(BasicBlock, BasicBlock), long> smoothedEdgeSamples)
        {
            MethodIL = methodIL;
            FlowGraph = fg;
            Samples = samples;
            SmoothedSamples = smoothedSamples;
            SmoothedEdgeSamples = smoothedEdgeSamples;
        }

        public MethodIL MethodIL { get; }
        public FlowGraph FlowGraph { get; }
        public Dictionary<BasicBlock, long> Samples { get; }
        public Dictionary<BasicBlock, long> SmoothedSamples { get; }
        public Dictionary<(BasicBlock, BasicBlock), long> SmoothedEdgeSamples { get; }

        /// <summary>
        /// Given pairs of runs (as relative IPs in this function), create a sample profile.
        /// </summary>
        public static SampleProfile CreateFromLbr(MethodIL il, NativeToILMap map, IEnumerable<(uint fromRva, uint toRva, long count)> runs)
        {
            FlowGraph fg = CreateFlowGraph(il);
            Dictionary<BasicBlock, long> bbSamples = fg.BasicBlocks.ToDictionary(bb => bb, bb => 0L);
            foreach ((uint from, uint to, long count) in runs)
            {
                foreach (BasicBlock bb in map.LookupRange(from, to).Select(fg.Lookup).Distinct())
                {
                    if (bb != null)
                        bbSamples[bb] += count;
                }
            }

            FlowSmoothing<BasicBlock> flowSmooth = new FlowSmoothing<BasicBlock>(bbSamples, fg.Lookup(0), bb => bb.Targets, (bb, isForward) => bb.Count * (isForward ? 1 : 50) + 2);
            flowSmooth.Perform();

            return new SampleProfile(il, fg, bbSamples, flowSmooth.NodeResults, flowSmooth.EdgeResults);
        }

        /// <summary>
        /// Given some IL offset samples into a method, construct a profile.
        /// </summary>
        public static SampleProfile Create(MethodIL il, IEnumerable<int> ilOffsetSamples)
        {
            FlowGraph fg = CreateFlowGraph(il);

            // Now associate raw IL-offset samples with basic blocks.
            Dictionary<BasicBlock, long> bbSamples = fg.BasicBlocks.ToDictionary(bb => bb, bb => 0L);
            foreach (int ofs in ilOffsetSamples.Where(o => o != -1 && fg.Lookup(o) != null))
            {
                BasicBlock bb = fg.Lookup(ofs);
                if (bb != null)
                    bbSamples[bb]++;
            }

            // Smooth the graph to produce something that satisfies flow conservation.
            FlowSmoothing<BasicBlock> flowSmooth = new FlowSmoothing<BasicBlock>(bbSamples, fg.Lookup(0), bb => bb.Targets, (bb, isForward) => bb.Count * (isForward ? 1 : 50) + 2);
            flowSmooth.Perform();

            return new SampleProfile(il, fg, bbSamples, flowSmooth.NodeResults, flowSmooth.EdgeResults);
        }

        private static FlowGraph CreateFlowGraph(MethodIL il)
        {
            // Start out by reconstructing the IL flow graph.
            HashSet<int> bbStarts = GetBasicBlockStarts(il);

            List<BasicBlock> bbs = new List<BasicBlock>();
            void AddBB(int start, int count)
            {
                if (count > 0)
                    bbs.Add(new BasicBlock(start, count));
            }

            int prevStart = 0;
            foreach (int ofs in bbStarts.OrderBy(o => o))
            {
                AddBB(prevStart, ofs - prevStart);
                prevStart = ofs;
            }

            AddBB(prevStart, il.GetILBytes().Length - prevStart);

            FlowGraph fg = new FlowGraph(bbs);

            // We know where each basic block starts now. Proceed by linking them together.
            ILReader reader = new ILReader(il.GetILBytes());
            foreach (BasicBlock bb in bbs)
            {
                reader.Seek(bb.Start);
                while (reader.HasNext)
                {
                    Debug.Assert(fg.Lookup(reader.Offset) == bb);
                    ILOpcode opc = reader.ReadILOpcode();
                    if (opc.IsBranch())
                    {
                        int tar = reader.ReadBranchDestination(opc);
                        bb.Targets.Add(fg.Lookup(tar));
                        if (!opc.IsUnconditionalBranch())
                            bb.Targets.Add(fg.Lookup(reader.Offset));

                        break;
                    }

                    if (opc == ILOpcode.switch_)
                    {
                        uint numCases = reader.ReadILUInt32();
                        int jmpBase = reader.Offset + checked((int)(numCases * 4));
                        bb.Targets.Add(fg.Lookup(jmpBase));

                        for (uint i = 0; i < numCases; i++)
                        {
                            int caseOfs = jmpBase + (int)reader.ReadILUInt32();
                            bb.Targets.Add(fg.Lookup(caseOfs));
                        }

                        break;
                    }

                    if (opc == ILOpcode.ret)
                    {
                        break;
                    }

                    reader.Skip(opc);
                    // Check fall through
                    if (reader.HasNext)
                    {
                        BasicBlock nextBB = fg.Lookup(reader.Offset);
                        if (nextBB != bb)
                        {
                            // Falling through
                            bb.Targets.Add(nextBB);
                            break;
                        }
                    }
                }
            }

            return fg;
        }

        /// <summary>
        /// Find IL offsets at which basic blocks begin.
        /// </summary>
        private static HashSet<int> GetBasicBlockStarts(MethodIL il)
        {
            ILReader reader = new ILReader(il.GetILBytes());
            HashSet<int> bbStarts = new HashSet<int>();
            bbStarts.Add(0);
            while (reader.HasNext)
            {
                ILOpcode opc = reader.ReadILOpcode();
                if (opc.IsBranch())
                {
                    int tar = reader.ReadBranchDestination(opc);
                    bbStarts.Add(tar);
                    // Conditional branches can fall through.
                    if (!opc.IsUnconditionalBranch())
                        bbStarts.Add(reader.Offset);
                }
                else if (opc == ILOpcode.switch_)
                {
                    uint numCases = reader.ReadILUInt32();
                    int jmpBase = reader.Offset + checked((int)(numCases * 4));
                    // Default case is at jmpBase.
                    bbStarts.Add(jmpBase);

                    for (uint i = 0; i < numCases; i++)
                    {
                        int caseOfs = jmpBase + (int)reader.ReadILUInt32();
                        bbStarts.Add(caseOfs);
                    }
                }
                else if (opc == ILOpcode.ret)
                {
                    if (reader.HasNext)
                        bbStarts.Add(reader.Offset);
                }
                else
                {
                    reader.Skip(opc);
                }
            }

            foreach (ILExceptionRegion ehRegion in il.GetExceptionRegions())
            {
                bbStarts.Add(ehRegion.TryOffset);
                bbStarts.Add(ehRegion.TryOffset + ehRegion.TryLength);
                bbStarts.Add(ehRegion.HandlerOffset);
                bbStarts.Add(ehRegion.HandlerOffset + ehRegion.HandlerLength);
                if (ehRegion.Kind.HasFlag(ILExceptionRegionKind.Filter))
                    bbStarts.Add(ehRegion.FilterOffset);
            }

            return bbStarts;
        }
    }

    internal class BasicBlock : IEquatable<BasicBlock>
    {
        public BasicBlock(int start, int count)
            => (Start, Count) = (start, count);

        // First IL offset
        public int Start { get; }
        // Number of IL bytes in this basic block
        public int Count { get; }

        public HashSet<BasicBlock> Targets { get; } = new HashSet<BasicBlock>();

        public override string ToString() => $"Start={Start}, Count={Count}";

        public override bool Equals(object obj) => Equals(obj as BasicBlock);
        public bool Equals(BasicBlock other) => other != null && Start == other.Start;
        public override int GetHashCode() => HashCode.Combine(Start);

        public static bool operator ==(BasicBlock left, BasicBlock right) => EqualityComparer<BasicBlock>.Default.Equals(left, right);
        public static bool operator !=(BasicBlock left, BasicBlock right) => !(left == right);
    }
}
