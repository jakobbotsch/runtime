// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Diagnostics.Tools.Pgo
{
    public sealed class CirculationGraph
    {

        public List<Node> Nodes;
        public List<Edge> Edges;

        public CirculationGraph()
        {
            this.Nodes = new List<Node>();
            this.Edges = new List<Edge>();
        }

        // Limit the stateful interface only to adding Nodes, the addition of Edges is implicit.
        // If all out-edges are added to the node before adding, the graph edge lists will be consistent.
        public void AddNode(Node toAdd)
        {
            this.Edges.AddRange(toAdd.OutEdgeList);
            this.Nodes.Add(toAdd);
        }

        public void CheckConsistentCirculation()
        {
            // First check that flow is within capacities.

            foreach (Edge e in this.Edges)
            {
                e.CheckEdgeConsistency();
            }
            // Then check that flow in = flow out.
            // Note that due to back-edges, each flow count should be 0.

            foreach (Node n in this.Nodes)
            {
                long inFlow = n.NetInFlow();
                long outFlow = n.NetOutFlow();
                if (inFlow != outFlow)
                {
                    throw new Exception(string.Format("Node {0}: Has in-flow of {1} and out-flow of {2}", n.ID, inFlow, outFlow));
                }
            }
        }

        public long TotalCirculationCost()
        {
            long totalCost = 0;
            foreach (Edge e in this.Edges)
            {
                totalCost += e.Cost * e.Flow;
            }
            // Divide the total by two because back-edges cause double-counting.

            return totalCost / 2;
        }

        // Outputs a DOT specification of the graph, only including edges with non-negative flow.
        public string OutputDOTGraph()
        {
            StringBuilder spec = new StringBuilder("digraph g {\n");
            // Output all the nodes annotated with their net flow.

            foreach (Node n in Nodes)
            {
                spec.Append(string.Format("node{0} [label={1}];\n", n.ID, n.NetInFlow()));
            }
            // Output the edges, along with their capacities/flow.

            foreach (Edge e in Edges)
            {
                if (e.Flow <= 0)
                {
                    continue;
                }
                spec.Append(string.Format("node{0} -> node{1} [label=\"{2}, [{3}, {4}]\"];\n", e.Source.ID, e.Target.ID, e.Flow, e.MinCapacity, e.MaxCapacity));
            }

            spec.Append("}");

            return spec.ToString();
        }

        public static void RunTests()
        {
            CirculationGraph g = new CirculationGraph();

            Node n1 = new Node();
            Node n2 = new Node();
            Node n3 = new Node();
            Node n4 = new Node();

            new Edge(n1, n2, 0, 3, 0);
            new Edge(n1, n3, 0, 3, 2);
            new Edge(n2, n3, 0, 1, 1);
            new Edge(n2, n4, 0, 1, 0);
            new Edge(n3, n4, 0, 2, -4);
            new Edge(n3, n1, 0, 10, 0);
            new Edge(n4, n1, 0, 3, -1);

            g.AddNode(n1);
            g.AddNode(n2);
            g.AddNode(n3);
            g.AddNode(n4);

            Console.WriteLine("Pre-Algorithm Cost: {0}", g.TotalCirculationCost());
            MinimumCostCirculation.FindMinCostCirculation(g);
            Console.WriteLine("Post-Algorithm Cost: {0}", g.TotalCirculationCost());
        }
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////////
    //
    // class Edge
    //
    ///////////////////////////////////////////////////////////////////////////////////////////////////

    public sealed class Edge
    {
        static int s_idCounter = 0;

        public Node Source;
        public Node Target;
        public Edge BackEdge;
        public long MinCapacity;
        public long MaxCapacity;
        public long Flow;
        public long Free;
        public long Cost;
        public long ID;

        public Edge(Node source, Node target, long minCapacity, long maxCapacity, long cost)
        {
            this.Source = source;
            this.Target = target;
            this.MinCapacity = minCapacity;
            this.MaxCapacity = maxCapacity;
            this.Flow = minCapacity;
            this.Free = maxCapacity - this.Flow;
            this.Cost = cost;
            this.ID = s_idCounter++;
            this.BackEdge = new Edge(this);

            // Make sure that the source and target Nodes of this edge know of its existence;
            // This asymmetry is because Node objects will be initialized first in building the graph.
            this.Target.AddInEdge(this);
            this.Source.AddOutEdge(this);
        }

        // Constructor to create backedge.
        public Edge(Edge backEdge)
        {
            this.Source = backEdge.Target;
            this.Target = backEdge.Source;
            this.BackEdge = backEdge;
            this.MinCapacity = -backEdge.MaxCapacity;
            this.MaxCapacity = -backEdge.MinCapacity;
            this.Flow = -backEdge.Flow;
            this.Free = this.MaxCapacity - this.Flow;
            this.Cost = -backEdge.Cost;
            this.ID = s_idCounter++;

            this.Target.AddInEdge(this);
            this.Source.AddOutEdge(this);
        }

        // Adds flow to the given edge and appropriately modifies backedge, throwing an exception if capacities are violated
        public void AddFlow(long delta)
        {
            if (this.Flow + delta < this.MinCapacity || this.Flow + delta > this.MaxCapacity)
            {
                throw new Exception(string.Format("Edge {0}: Tried to assign flow of {1} with capacity range [{2}, {3}]", this.ID, this.Flow + delta, this.MinCapacity, this.MaxCapacity));
            }
            this.Flow += delta;
            this.Free -= delta;
            this.BackEdge.Flow -= delta;
            this.BackEdge.Free += delta;
        }

        // Checks whether flow is within the capacities, and that the backedge is consistent.
        public void CheckEdgeConsistency()
        {
            if (this.Flow < this.MinCapacity || this.Flow > this.MaxCapacity)
            {
                throw new Exception(string.Format("Edge {0}: Flow of {1} falls outside of capacity range [{2}, {3}]", this.ID, this.Flow, this.MinCapacity, this.MaxCapacity));
            }

            if (this.Free != this.MaxCapacity - this.Flow)
            {
                throw new Exception(string.Format("Edge {0}: Annotated with {1} free capacity, while should have {2}", this.ID, this.Free, this.MaxCapacity - this.Flow));
            }

            if (this.Flow != -this.BackEdge.Flow)
            {
                throw new Exception(string.Format("Edge {0}: Has {1} flow while backedge has {2}", this.ID, this.Flow, this.BackEdge.Flow));
            }
        }

        public override string ToString() => $"{Source} -> {Target}";
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////////
    //
    // class Node
    //
    ///////////////////////////////////////////////////////////////////////////////////////////////////

    public sealed class Node
    {
        static int s_idCounter = 0;

        public List<Edge> InEdgeList;
        public List<Edge> OutEdgeList;
        public Dictionary<Node, Edge> InEdgeMap;
        public Dictionary<Node, Edge> OutEdgeMap;
        public NodeMetaData MetaData;
        public int ID;

        public Node()
        {
            this.InEdgeList = new List<Edge>();
            this.OutEdgeList = new List<Edge>();
            this.InEdgeMap = new Dictionary<Node, Edge>();
            this.OutEdgeMap = new Dictionary<Node, Edge>();
            this.MetaData = new NodeMetaData();
            this.ID = s_idCounter++;
        }

        public void AddInEdge(Edge toAdd)
        {
            this.InEdgeList.Add(toAdd);
            this.InEdgeMap[toAdd.Target] = toAdd;
        }

        public void AddOutEdge(Edge toAdd)
        {
            this.OutEdgeList.Add(toAdd);
            this.OutEdgeMap[toAdd.Target] = toAdd;
        }

        public long NetInFlow()
        {
            long inFlow = 0;
            foreach (Edge inEdge in this.InEdgeList)
            {
                // Only count positive flow edges to derive net in-flow (every positive edge has a negative back-edge)

                inFlow += Math.Max(0, inEdge.Flow);
            }
            return inFlow;
        }

        public long NetOutFlow()
        {
            long outFlow = 0;
            foreach (Edge outEdge in this.OutEdgeList)
            {
                outFlow += Math.Max(0, outEdge.Flow);
            }
            return outFlow;
        }

        public override string ToString() => $"{ID}";
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////////
    //
    // class NodeMetaData
    //
    ///////////////////////////////////////////////////////////////////////////////////////////////////

    // This class carries around the meta-data to expedite Bellman-Ford's minimum cost algorithm in MinimumCostCirculation.
    public sealed class NodeMetaData
    {
        public long Distance;
        public Edge PredEdge;

        public NodeMetaData()
        {
            this.Distance = 0;
            this.PredEdge = null;
        }
    }
}
