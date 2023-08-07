using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Pool;

namespace RecastSharp
{
    public class ServerSpan
    {
        private static readonly ObjectPool<ServerSpan> Pool = new(CreateServerSpan, null, ReleaseServerSpan);

        public ushort smin; //下表面
        public ushort smax; //上表面
        public byte mask; //类型

        public ServerSpan next;

        public ulong GetResult()
        {
            return ((ulong)smin << 48) | ((ulong)smax << 32) | ((ulong)mask << 24);
        }

        public static ServerSpan Create()
        {
            return Pool.Get();
        }

        public void Release()
        {
            ServerSpan n = this;
            while (n != null)
            {
                Pool.Release(n);
                var pre = n;
                n = n.next;
                pre.next = null;
            }
        }

        public string GetUniqueKey()
        {
            StringBuilder keyBuilder = new StringBuilder();
            ServerSpan span = this;
            while (span != null)
            {
                keyBuilder.Append(span.GetResult());
                span = span.next;
            }

            return keyBuilder.ToString();
        }


        private static ServerSpan CreateServerSpan()
        {
            return new ServerSpan();
        }

        private static void ReleaseServerSpan(ServerSpan span)
        {
            span.smin = 0;
            span.smax = 0;
            span.mask = 0;
        }

        public static void ReleaseAll()
        {
            Pool.Clear();
        }
    }

    public class MergedServerSpan
    {
        public uint index;
        public byte count;
        public ServerSpan span;
        public List<uint> columnIndices; // 合并的体素格子的列索引
    }

    public struct ServerCell
    {
        public byte count; // span数量
        public uint index; // span起始索引
    }

    public class ServerMapVoxel
    {
        public int voxelCellX; // x最大坐标
        public int voxelMaxHeight; // y最大坐标
        public int voxelCellZ; // z最大坐标
        public ServerCell[] cells; // 地图xy平面格子

        public void Init(Recast.rcHeightfield hf)
        {
            voxelCellX = hf.width - 1;
            voxelMaxHeight = Mathf.CeilToInt(hf.bmax[1] / hf.ch) - 1;
            voxelCellZ = hf.height - 1;
            cells = new ServerCell[hf.width * hf.height];
        }
    }
}