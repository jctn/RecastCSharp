using System.Collections.Generic;

namespace RecastSharp
{
    /// <summary>
    /// Domain-independent, rectangular clusters
    /// </summary>
    public class Cluster
    {
        //Boundaries of the cluster (with respect to the original map)
        public readonly Boundaries Boundaries = new();
        public readonly Dictionary<GridTile, Node> Nodes = new();

        //Clusters from the lower level
        public List<Cluster> Clusters;

        public int Width;
        public int Height;

        //Check if this cluster contains the other cluster (by looking at boundaries)
        public bool Contains(Cluster other)
        {
            return other.Boundaries.Min.x >= Boundaries.Min.x &&
                   other.Boundaries.Min.y >= Boundaries.Min.y &&
                   other.Boundaries.Max.x <= Boundaries.Max.x &&
                   other.Boundaries.Max.y <= Boundaries.Max.y;
        }

        public bool Contains(GridTile pos)
        {
            return pos.x >= Boundaries.Min.x &&
                   pos.x <= Boundaries.Max.x &&
                   pos.y >= Boundaries.Min.y &&
                   pos.y <= Boundaries.Max.y;
        }
    }
}