using System;
using System.Runtime.InteropServices;

namespace RecastSharp
{
    public struct ServerSpan
    {
        public ushort smin; //下表面
        public ushort smax; //上表面
        public byte mask; //类型
    }

    public struct ServerCell
    {
        public ushort spanCnt; // span数量
        public uint minSpanIdx; // span起始索引
    }

    public struct ServerMapVoxel
    {
        public ushort maxLength; // x最大坐标
        public ushort maxWidth; // y最大坐标
        public ushort maxHeight; // z最大坐标
        public byte walkableHeight; // 可行高度 (体素上下表面差)
        public byte walkableClimb; // 攀爬高度 (体素上下表面差)
        public uint spanCnt; // 总span数量
        public ServerSpan[] cells; // 地图xy平面格子
        public ServerCell[] spans; // 地图体素数据
    }
   
}