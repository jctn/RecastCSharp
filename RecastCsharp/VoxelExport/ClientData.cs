using System;

namespace RecastSharp
{
    public class ClientMapVoxel
    {
        public float voxelSize;
        public float voxelHeight;
        public int mapX;
        public int mapY;
        public int mapZ;
        public int regionAxisNum;
        public ushort regionNum;
        public ushort regionWidth;
        public ushort regionHeight;
        public RegionVoxelData[] regions;

        public void Init(Recast.rcHeightfield hf, int regionSize)
        {
            voxelSize = hf.cs;
            voxelHeight = hf.ch;
            mapX = (int)(hf.width * hf.cs);
            mapY = (int)hf.bmax[1];
            mapZ = (int)(hf.width * hf.cs);
            regionAxisNum = regionSize;
            regionWidth = (ushort)Math.Ceiling(hf.width * 1.0f / regionSize);
            regionHeight = (ushort)Math.Ceiling(hf.height * 1.0f / regionSize);
            regionNum = (ushort)(regionWidth * regionHeight);
            regions = new RegionVoxelData[regionNum];
        }
    }

    public class RegionVoxelData
    {
        public int index;
        public int cellWidthNum;
        public int cellHeightNum;
        public int totalSpanNum;
        public int mergeSpanNum;
        public byte[] cellSpanCountArr;
        public uint[] cellSpanIndexArr;
        public VoxelSpan[] spans;
        public byte[] cellConnectFlags;
    }

    public class JsonMapVoxel
    {
        public float voxelSize;
        public float voxelHeight;
        public int mapX;
        public int mapY;
        public int mapZ;
        public int regionSize;
        public ushort regionNum;
        public ushort regionWidth;
        public ushort regionHeight;
        public JsonRegionVoxelData[] regions;

        public void Init(Recast.rcHeightfield hf, int regionSize)
        {
            voxelSize = hf.cs;
            voxelHeight = hf.ch;
            mapX = (int)(hf.width * hf.cs);
            mapY = (int)hf.bmax[1];
            mapZ = (int)(hf.width * hf.cs);
            this.regionSize = regionSize;
            regionWidth = (ushort)Math.Ceiling(hf.width * 1.0f / regionSize);
            regionHeight = (ushort)Math.Ceiling(hf.height * 1.0f / regionSize);
            regionNum = (ushort)(regionWidth * regionHeight);
            regions = new JsonRegionVoxelData[regionNum];
        }
    }

    public class JsonRegionVoxelData
    {
        public int cellWidth;
        public int cellHeight;
        public CellSpanInfo[] spans;
    }


    public class CellSpanInfo
    {
        public int x;
        public int z;
        public VoxelSpan[] spans;

        public CellSpanInfo(int x, int z, int spanNum)
        {
            this.x = x;
            this.z = z;
            if (spanNum > 0)
            {
                spans = new VoxelSpan[spanNum];
            }
        }
    }

    public class VoxelSpan
    {
        public readonly ushort Min;
        public readonly ushort Max;
        public readonly byte Area;
        public readonly byte Offset;

        public VoxelSpan(ushort min, ushort max, byte area, byte offset = 0)
        {
            Min = min;
            Max = max;
            Area = area;
            Offset = offset;
        }

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            VoxelSpan other = (VoxelSpan)obj;

            return Min == other.Min && Max == other.Max && Area == other.Area && Offset == other.Offset;
        }

        public override int GetHashCode()
        {
            ulong hashCode = (ulong)Min.GetHashCode() << 32 | (ulong)Max.GetHashCode() << 16 |
                             (uint)Area.GetHashCode() << 8 | Offset;
            return hashCode.GetHashCode();
        }
    }
}