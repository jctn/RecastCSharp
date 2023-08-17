using System.Collections.Generic;

namespace RecastSharp
{
    public class Edge
    {
        public Node start;
        public Node end;
        public EdgeType type;
        public float weight;

        public LinkedList<Edge> UnderlyingPath;
    }

    public enum EdgeType
    {
        Intra,
        Inter
    }
}