using System.Collections.Generic;

namespace RecastSharp
{
    /// <summary>
    /// 边，节点之间的连接
    /// </summary>
    public class Edge
    {
        public Node Start;
        public Node End;
        public EdgeType Type;
        public float Weight;

        public LinkedList<Edge> UnderlyingPath;
    }

    public enum EdgeType
    {
        Intra, //Connections between same cluster nodes
        Inter //Connection between other cluster nodes 

    }
}