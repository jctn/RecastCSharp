namespace RecastSharp
{
    public class ReverseSpan
    {
        public static uint ReverseSpanCount;

        public ushort smin;
        public ushort smax;
        public byte flag;
        public ReverseSpan link;
        public ReverseSpan next;
    }
}