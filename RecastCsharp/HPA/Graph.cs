using System.Collections.Generic;
using UnityEngine;

namespace RecastSharp
{
    public class Graph
    {
        public static readonly float Sqrt2 = Mathf.Sqrt(2f);

        public int depth;

        //List of clusters for every level of abstraction
        private readonly List<Cluster>[] _levelClusters;

        //Keep a representation of the map by low level nodes
        private readonly Dictionary<GridTile, Node> _nodes;

        readonly int _width;
        readonly int _height;
        private byte[] _connectFlags;

        public List<Cluster>[] levelClusters => _levelClusters;

        public Graph(int w, int h, int maxLevel, int clusterSize, byte[] connectFlags)
        {
            depth = maxLevel;
            _width = w;
            _height = h;
            _connectFlags = connectFlags;
            _nodes = new Dictionary<GridTile, Node>(w * h);


            //1. Create all nodes necessary
            for (int x = 0; x < w; ++x)
            {
                for (int z = 0; z < h; ++z)
                {
                    if (connectFlags[x + z * w] == 0)
                        continue;
                    var gridTile = new GridTile(x, z);
                    _nodes.Add(gridTile, new Node(gridTile));
                }
            }

            //2. Create all possible edges
            foreach (Node n in _nodes.Values)
            {
                //Look for straight edges
                for (int i = -1; i < 2; i += 2)
                {
                    SearchMapEdge(_nodes, n, n.pos.x + i, n.pos.y, false);

                    SearchMapEdge(_nodes, n, n.pos.x, n.pos.y + i, false);
                }

                //Look for diagonal edges
                for (int i = -1; i < 2; i += 2)
                {
                    for (int j = -1; j < 2; j += 2)
                    {
                        SearchMapEdge(_nodes, n, n.pos.x + i, n.pos.y + j, true);
                    }
                }
            }


            _levelClusters = new List<Cluster>[maxLevel];

            for (int i = 0; i < maxLevel; ++i)
            {
                if (i != 0)
                    //Increment cluster size for higher levels
                    clusterSize *= 2;

                //Set number of clusters in horizontal and vertical direction
                var clusterHeight = Mathf.CeilToInt((float)h / clusterSize);
                var clusterWidth = Mathf.CeilToInt((float)w / clusterSize);

                if (clusterWidth <= 1 && clusterHeight <= 1)
                {
                    //A ClusterWidth or clusterHeight of 1 means there is only going to be one cluster in this direction.
                    //Therefore if both are 1 then this level is useless 
                    depth = i;
                    break;
                }

                _levelClusters[i] = BuildClusters(i, clusterSize, clusterWidth, clusterHeight);
            }
        }

        /// <summary>
        /// Add the edge to the node if it's a valid map edge
        /// </summary>
        private void SearchMapEdge(Dictionary<GridTile, Node> nodes, Node n, int x, int y, bool diagonal)
        {
            //Don't let diagonal movement occur when an obstacle is crossing the edge
            if (diagonal)
            {
                if (!IsWalkableTitle(n.pos.x, y)) return;
                if (!IsWalkableTitle(x, n.pos.y)) return;
            }

            if (!IsWalkableTitle(x, y)) return;
            GridTile gridTile = new GridTile(x, y);

            //Edge is valid, add it to the node
            n.edges.Add(new Edge()
            {
                Start = n,
                End = nodes[gridTile],
                Type = EdgeType.Inter,
                Weight = diagonal ? Sqrt2 : 1f,
            });
        }

        private bool IsWalkableTitle(int x, int y)
        {
            return x >= 0 && x < _width && y >= 0 && y < _height && _connectFlags[x + y * _width] != 0;
        }

        private delegate void CreateBorderNodes(Cluster c1, Cluster c2, bool x);

        private void CreateAbstractBorderNodes(Cluster p1, Cluster p2, bool x)
        {
            foreach (Cluster c1 in p1.Clusters)
            {
                foreach (Cluster c2 in p2.Clusters)
                {
                    if ((x && c1.Boundaries.Min.y == c2.Boundaries.Min.y &&
                         c1.Boundaries.Max.x + 1 == c2.Boundaries.Min.x) ||
                        (!x && c1.Boundaries.Min.x == c2.Boundaries.Min.x &&
                         c1.Boundaries.Max.y + 1 == c2.Boundaries.Min.y))
                    {
                        CreateAbstractInterEdges(p1, p2, c1, c2);
                    }
                }
            }
        }

        private void CreateAbstractInterEdges(Cluster p1, Cluster p2, Cluster c1, Cluster c2)
        {
            List<Edge> edges1 = new(),
                edges2 = new();
            Node n1, n2;

            //Add edges that connects them from c1
            foreach (KeyValuePair<GridTile, Node> n in c1.Nodes)
            {
                foreach (Edge e in n.Value.edges)
                {
                    if (e.Type == EdgeType.Inter && c2.Contains(e.End.pos))
                        edges1.Add(e);
                }
            }

            foreach (KeyValuePair<GridTile, Node> n in c2.Nodes)
            {
                foreach (Edge e in n.Value.edges)
                {
                    if (e.Type == EdgeType.Inter && c1.Contains(e.End.pos))
                        edges2.Add(e);
                }
            }

            //Find every pair of twin edges and insert them in their respective parents
            foreach (Edge e1 in edges1)
            {
                foreach (Edge e2 in edges2)
                {
                    if (e1.End == e2.Start)
                    {
                        if (!p1.Nodes.TryGetValue(e1.Start.pos, out n1))
                        {
                            n1 = new Node(e1.Start.pos) { child = e1.Start };
                            p1.Nodes.Add(n1.pos, n1);
                        }

                        if (!p2.Nodes.TryGetValue(e2.Start.pos, out n2))
                        {
                            n2 = new Node(e2.Start.pos) { child = e2.Start };
                            p2.Nodes.Add(n2.pos, n2);
                        }

                        n1.edges.Add(new Edge() { Start = n1, End = n2, Type = EdgeType.Inter, Weight = 1 });
                        n2.edges.Add(new Edge() { Start = n2, End = n1, Type = EdgeType.Inter, Weight = 1 });

                        break; //Break the second loop since we've found a pair
                    }
                }
            }
        }

        private List<Cluster> BuildClusters(int level, int clusterSize, int clusterWidth, int clusterHeight)
        {
            List<Cluster> clusters = new List<Cluster>();

            int i, j;

            //Create clusters of this level
            for (i = 0; i < clusterHeight; ++i)
            {
                for (j = 0; j < clusterWidth; ++j)
                {
                    var cluster = new Cluster
                    {
                        Boundaries =
                        {
                            Min = new GridTile(j * clusterSize, i * clusterSize)
                        }
                    };
                    cluster.Boundaries.Max = new GridTile(
                        Mathf.Min(cluster.Boundaries.Min.x + clusterSize - 1, _width - 1),
                        Mathf.Min(cluster.Boundaries.Min.y + clusterSize - 1, _height - 1));

                    //Adjust size of cluster based on boundaries
                    cluster.Width = cluster.Boundaries.Max.x - cluster.Boundaries.Min.x + 1;
                    cluster.Height = cluster.Boundaries.Max.y - cluster.Boundaries.Min.y + 1;

                    if (level > 0)
                    {
                        //Since we're abstract, we will have lower level clusters
                        cluster.Clusters = new List<Cluster>();

                        //Add lower level clusters in newly created clusters
                        var lowLevelClusters = _levelClusters[level - 1];
                        foreach (Cluster c in lowLevelClusters)
                        {
                            if (cluster.Contains(c))
                            {
                                cluster.Clusters.Add(c);
                            }
                        }
                    }

                    clusters.Add(cluster);
                }
            }


            if (level == 0)
            {
                //Add border nodes for every adjacent pair of clusters
                for (i = 0; i < clusters.Count; ++i)
                {
                    for (j = i + 1; j < clusters.Count; ++j)
                    {
                        DetectAdjacentClusters(clusters[i], clusters[j], CreateConcreteBorderNodes);
                    }
                }
            }
            else
            {
                //Add border nodes for every adjacent pair of clusters
                for (i = 0; i < clusters.Count; ++i)
                {
                    for (j = i + 1; j < clusters.Count; ++j)
                    {
                        DetectAdjacentClusters(clusters[i], clusters[j], CreateAbstractBorderNodes);
                    }
                }
            }

            //Add Intra edges for every border nodes and pathfinding between them
            for (i = 0; i < clusters.Count; ++i)
            {
                GenerateIntraEdges(clusters[i]);
            }

            return clusters;
        }

        /// <summary>
        /// Create border nodes and attach them together.
        /// We always pass the lower cluster first (in c1).
        /// Adjacent index : if x == true, then c1.BottomRight.x else c1.BottomRight.y
        /// </summary>
        private void CreateConcreteBorderNodes(Cluster c1, Cluster c2, bool x)
        {
            int i, iMin, iMax;
            if (x)
            {
                iMin = c1.Boundaries.Min.y;
                iMax = iMin + c1.Height;
            }
            else
            {
                iMin = c1.Boundaries.Min.x;
                iMax = iMin + c1.Width;
            }

            int lineSize = 0;
            for (i = iMin; i < iMax; ++i)
            {
                if (x && (_nodes.ContainsKey(new GridTile(c1.Boundaries.Max.x, i)) &&
                          _nodes.ContainsKey(new GridTile(c2.Boundaries.Min.x, i)))
                    || !x && (_nodes.ContainsKey(new GridTile(i, c1.Boundaries.Max.y)) &&
                              _nodes.ContainsKey(new GridTile(i, c2.Boundaries.Min.y))))
                {
                    lineSize++;
                }
                else
                {
                    CreateConcreteInterEdges(c1, c2, x, ref lineSize, i);
                }
            }

            //If line size > 0 after looping, then we have another line to fill in
            CreateConcreteInterEdges(c1, c2, x, ref lineSize, i);
        }

        //i is the index at which we stopped (either its an obstacle or the end of the cluster)
        private void CreateConcreteInterEdges(Cluster c1, Cluster c2, bool x, ref int lineSize, int i)
        {
            if (lineSize > 0)
            {
                if (lineSize <= 5)
                {
                    //Line is too small, create 1 inter edges
                    CreateConcreteInterEdge(c1, c2, x, i - (lineSize / 2 + 1));
                }
                else
                {
                    //Create 2 inter edges
                    CreateConcreteInterEdge(c1, c2, x, i - lineSize);
                    CreateConcreteInterEdge(c1, c2, x, i - 1);
                }

                lineSize = 0;
            }
        }

        //Inter edges are edges that crosses clusters
        private void CreateConcreteInterEdge(Cluster c1, Cluster c2, bool x, int i)
        {
            GridTile g1, g2;
            if (x)
            {
                g1 = new GridTile(c1.Boundaries.Max.x, i);
                g2 = new GridTile(c2.Boundaries.Min.x, i);
            }
            else
            {
                g1 = new GridTile(i, c1.Boundaries.Max.y);
                g2 = new GridTile(i, c2.Boundaries.Min.y);
            }

            if (!c1.Nodes.TryGetValue(g1, out var n1))
            {
                n1 = new Node(g1);
                c1.Nodes.Add(g1, n1);
                n1.child = _nodes[g1];
            }

            if (!c2.Nodes.TryGetValue(g2, out var n2))
            {
                n2 = new Node(g2);
                c2.Nodes.Add(g2, n2);
                n2.child = _nodes[g2];
            }

            n1.edges.Add(new Edge() { Start = n1, End = n2, Type = EdgeType.Inter, Weight = 1 });
            n2.edges.Add(new Edge() { Start = n2, End = n1, Type = EdgeType.Inter, Weight = 1 });
        }

        private void DetectAdjacentClusters(Cluster c1, Cluster c2, CreateBorderNodes createBorderNodes)
        {
            //Check if both clusters are adjacent
            if (c1.Boundaries.Min.x == c2.Boundaries.Min.x)
            {
                //c2 is top of c1
                if (c1.Boundaries.Max.y + 1 == c2.Boundaries.Min.y)
                {
                    createBorderNodes(c1, c2, false);
                }
                //c2 is bottom of c1
                else if (c2.Boundaries.Max.y + 1 == c1.Boundaries.Min.y)
                {
                    createBorderNodes(c2, c1, false);
                }
            }
            else if (c1.Boundaries.Min.y == c2.Boundaries.Min.y)
            {
                //c2 is right of c1
                if (c1.Boundaries.Max.x + 1 == c2.Boundaries.Min.x)
                {
                    createBorderNodes(c1, c2, true);
                }
                //c2 is left of c1
                else if (c2.Boundaries.Max.x + 1 == c1.Boundaries.Min.x)
                {
                    createBorderNodes(c2, c1, true);
                }
            }
        }

        //Intra edges are edges that lives inside clusters
        private void GenerateIntraEdges(Cluster c)
        {
            int i, j;

            //We do this so that we can iterate through pairs once,
            //by keeping the second index always higher than the first
            var nodes = new List<Node>(c.Nodes.Values);

            for (i = 0; i < nodes.Count; ++i)
            {
                var n1 = nodes[i];
                for (j = i + 1; j < nodes.Count; ++j)
                {
                    var n2 = nodes[j];

                    ConnectNodes(n1, n2, c);
                }
            }
        }

        /// <summary>
        /// Connect two nodes by pathfinding between them. 
        /// </summary>
        /// <remarks>We assume they are different nodes. If the path returned is 0, then there is no path that connects them.</remarks>
        private bool ConnectNodes(Node n1, Node n2, Cluster c)
        {
            LinkedListNode<Edge> iter;

            float weight = 0f;

            var path = PathFinder.FindPath(n1.child, n2.child, c.Boundaries);

            if (path.Count > 0)
            {
                var e1 = new Edge()
                {
                    Start = n1,
                    End = n2,
                    Type = EdgeType.Intra,
                    UnderlyingPath = path
                };

                var e2 = new Edge()
                {
                    Start = n2,
                    End = n1,
                    Type = EdgeType.Intra,
                    UnderlyingPath = new LinkedList<Edge>()
                };

                //Store inverse path in node n2
                //Sum weights of underlying edges at the same time
                iter = e1.UnderlyingPath.Last;
                while (iter != null)
                {
                    // Find twin edge
                    var val = iter.Value.End.edges.Find(
                        e => e.Start == iter.Value.End && e.End == iter.Value.Start);

                    e2.UnderlyingPath.AddLast(val);
                    weight += val.Weight;
                    iter = iter.Previous;
                }

                //Update weights
                e1.Weight = weight;
                e2.Weight = weight;

                n1.edges.Add(e1);
                n2.edges.Add(e2);

                return true;
            }

            //No path, return false
            return false;
        }
    }
}