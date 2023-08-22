using System.Collections.Generic;

namespace RecastSharp
{
    public class Node
    {
        public GridTile pos;
        public List<Edge> edges;
        public Node child;

        public Node(GridTile value)
        {
            pos = value;
            edges = new List<Edge>();
        }
    }
}