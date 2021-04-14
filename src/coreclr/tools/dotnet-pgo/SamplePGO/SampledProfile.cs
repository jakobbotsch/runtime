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
    internal class SampledProfile
    {
        public List<BasicBlock> BasicBlocks { get; }
        public Dictionary<BasicBlock, long> SmoothedSamples { get; }
        public Dictionary<(BasicBlock, BasicBlock), long> SmoothedEdgeSamples { get; }
        public Dictionary<BasicBlock, >

        /// <summary>
        /// Given some IL offset samples into a method, construct a profile of edge probabilities.
        /// </summary>
        public static SampledProfile Create(EcmaMethod method, IEnumerable<int> ilOffsetSamples)
        {
            // Start out by reconstructing the IL flow graph.
            EcmaMethodIL il = EcmaMethodIL.Create(method);
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

            int[] bbKeys = bbs.Select(bb => bb.Start).ToArray();

            BasicBlock LookupBasicBlock(int ilOffset)
            {
                int index = Array.BinarySearch(bbKeys, ilOffset);
                BasicBlock bb = bbs[index >= 0 ? index : (~index - 1)];
                Debug.Assert(ilOffset >= bb.Start && ilOffset < bb.Start + bb.Count);
                return bb;
            }

            // We know where each basic block starts now. Proceed by linking them together.
            ILReader reader = new ILReader(il.GetILBytes());
            foreach (BasicBlock bb in bbs)
            {
                reader.Seek(bb.Start);
                while (reader.HasNext)
                {
                    Debug.Assert(LookupBasicBlock(reader.Offset) == bb);
                    ILOpcode opc = reader.ReadILOpcode();
                    if (opc.IsBranch())
                    {
                        int tar = reader.ReadBranchDestination(opc);
                        bb.Targets.Add(LookupBasicBlock(tar));
                        if (!opc.IsUnconditionalBranch())
                            bb.Targets.Add(LookupBasicBlock(reader.Offset));

                        break;
                    }

                    if (opc == ILOpcode.switch_)
                    {
                        uint numCases = reader.ReadILUInt32();
                        int jmpBase = reader.Offset + checked((int)(numCases * 4));
                        bb.Targets.Add(LookupBasicBlock(jmpBase));

                        for (uint i = 0; i < numCases; i++)
                        {
                            int caseOfs = jmpBase + (int)reader.ReadILUInt32();
                            bb.Targets.Add(LookupBasicBlock(caseOfs));
                        }

                        break;
                    }

                    reader.Skip(opc);
                    // Check fall through
                    if (reader.HasNext)
                    {
                        BasicBlock nextBB = LookupBasicBlock(reader.Offset);
                        if (nextBB != bb)
                        {
                            // Falling through
                            bb.Targets.Add(nextBB);
                            break;
                        }
                    }
                }
            }

            // Now associate raw IL-offset samples with basic blocks.
            Dictionary<BasicBlock, long> bbSamples = bbs.ToDictionary(bb => bb, bb => 0L);
            foreach (int ofs in ilOffsetSamples.Where(o => o != -1))
                CollectionsMarshal.GetValueRefOrNullRef(bbSamples, LookupBasicBlock(ofs))++;

            // Smooth the graph to produce something that satisfies flow conservation.
            FlowSmoothing<BasicBlock> flowSmooth = new FlowSmoothing<BasicBlock>(bbSamples, LookupBasicBlock(0), bb => bb.Targets, (bb, isForward) => bb.Count * (isForward ? 1 : 50) + 2);
            flowSmooth.Perform();

            return null;
        }

        private static string DumpGraph(List<BasicBlock> bbs, Dictionary<BasicBlock, long> numSamples, FlowSmoothing<BasicBlock> smoothed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("digraph G {");
            sb.AppendLine("  forcelabels=true;");
            sb.AppendLine();
            Dictionary<BasicBlock, int> bbToIndex = new Dictionary<BasicBlock, int>();
            for (int i = 0; i < bbs.Count; i++)
                bbToIndex.Add(bbs[i], i);

            foreach (BasicBlock bb in bbs)
            {
                //string label = $"Samples: {(numSamples.TryGetValue(bb, out long ns) ? ns : 0)}\\nSmoothed samples: {smoothed.NodeResults[bb]}";
                string label = smoothed.NodeResults[bb].ToString();
                //if (numSamples == null)
                //    label = $"#{ilToIndex[bb.Start]} @ {bb.Start} -> {bb.Start + bb.Count}";
                //else
                //    label = (numSamples.TryGetValue(bb, out int ns) ? ns : 0).ToString();

                sb.AppendLine($"  BB{bbToIndex[bb]} [label=\"{label}\"];");
            }

            sb.AppendLine();

            foreach (BasicBlock bb in bbs)
            {
                foreach (BasicBlock tar in bb.Targets)
                {
                    string label = smoothed.EdgeResults[(bb, tar)].ToString();
                    sb.AppendLine($"  BB{bbToIndex[bb]} -> BB{bbToIndex[tar]} [label=\"{label}\"];");
                }
            }

            // Write ranks with BFS.
            List<BasicBlock> curRank = new List<BasicBlock> { bbs.Single(bb => bb.Start == 0) };
            HashSet<BasicBlock> seen = new HashSet<BasicBlock>(curRank);
            while (curRank.Count > 0)
            {
                sb.AppendLine($"  {{rank = same; {string.Concat(curRank.Select(bb => $"BB{bbToIndex[bb]}; "))}}}");
                curRank = curRank.SelectMany(bb => bb.Targets).Where(seen.Add).ToList();
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Find IL offsets at which basic blocks begin.
        /// </summary>
        private static HashSet<int> GetBasicBlockStarts(EcmaMethodIL il)
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

        private class BasicBlock : IEquatable<BasicBlock>
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
}
