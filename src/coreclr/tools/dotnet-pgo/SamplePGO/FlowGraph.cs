// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Tools.Pgo
{
    internal class FlowGraph
    {
        private readonly int[] _bbKeys;

        public FlowGraph(IEnumerable<BasicBlock> bbs)
        {
            BasicBlocks = bbs.OrderBy(bb => bb.Start).ToList();
            _bbKeys = BasicBlocks.Select(bb => bb.Start).ToArray();
        }

        /// <summary>Basic blocks, ordered by start IL offset.</summary>
        public List<BasicBlock> BasicBlocks { get; }

        /// <summary>Find index of basic block containing IL offset.</summary>
        public int LookupIndex(int ilOffset)
        {
            int index = Array.BinarySearch(_bbKeys, ilOffset);
            if (index < 0)
                index = ~index - 1;

            // If ilOffset is negative (more generally, before the first BB)
            // then binarySearch will return ~0 since index 0 is the first BB
            // that's greater.
            if (index < 0)
                return -1;

            // If this is the last BB we could be after as well.
            BasicBlock bb = BasicBlocks[index];
            if (ilOffset >= bb.Start + bb.Count)
                return -1;

            return index;
        }

        public BasicBlock Lookup(int ilOffset)
            => LookupIndex(ilOffset) switch
            {
                -1 => null,
                int idx => BasicBlocks[idx]
            };

        public IEnumerable<BasicBlock> LookupRange(int ilOffsetStart, int ilOffsetEnd)
        {
            if (ilOffsetStart < BasicBlocks[0].Start)
                ilOffsetStart = BasicBlocks[0].Start;

            if (ilOffsetEnd > BasicBlocks.Last().Start)
                ilOffsetEnd = BasicBlocks.Last().Start;

            int end = LookupIndex(ilOffsetEnd);
            for (int i = LookupIndex(ilOffsetStart); i <= end; i++)
                yield return BasicBlocks[i];
        }

        internal string Dump(Func<BasicBlock, string> getNodeAnnot, Func<(BasicBlock, BasicBlock), string> getEdgeAnnot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("digraph G {");
            sb.AppendLine("  forcelabels=true;");
            sb.AppendLine();
            Dictionary<long, int> bbToIndex = new Dictionary<long, int>();
            for (int i = 0; i < BasicBlocks.Count; i++)
                bbToIndex.Add(BasicBlocks[i].Start, i);

            foreach (BasicBlock bb in BasicBlocks)
            {
                //string label = $"Samples: {(numSamples.TryGetValue(bb, out long ns) ? ns : 0)}\\nSmoothed samples: {smoothed.NodeResults[bb]}";
                string label = $"[{bb.Start:x}..{bb.Start + bb.Count:x})\\n{getNodeAnnot(bb)}";
                //if (numSamples == null)
                //    label = $"#{ilToIndex[bb.Start]} @ {bb.Start} -> {bb.Start + bb.Count}";
                //else
                //    label = (numSamples.TryGetValue(bb, out int ns) ? ns : 0).ToString();

                sb.AppendLine($"  BB{bbToIndex[bb.Start]} [label=\"{label}\"];");
            }

            sb.AppendLine();

            foreach (BasicBlock bb in BasicBlocks)
            {
                foreach (BasicBlock tar in bb.Targets)
                {
                    string label = getEdgeAnnot((bb, tar));
                    string postfix = string.IsNullOrEmpty(label) ? "" : $" [label=\"{label}\"]";
                    sb.AppendLine($"  BB{bbToIndex[bb.Start]} -> BB{bbToIndex[tar.Start]}{postfix};");
                }
            }

            // Write ranks with BFS.
            List<BasicBlock> curRank = new List<BasicBlock> { BasicBlocks.Single(bb => bb.Start == 0) };
            HashSet<BasicBlock> seen = new HashSet<BasicBlock>(curRank);
            while (curRank.Count > 0)
            {
                sb.AppendLine($"  {{rank = same; {string.Concat(curRank.Select(bb => $"BB{bbToIndex[bb.Start]}; "))}}}");
                curRank = curRank.SelectMany(bb => bb.Targets).Where(seen.Add).ToList();
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

    }
}
