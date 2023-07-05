using System;
using System.Collections.Generic;
using UnityEngine;

namespace RecastSharp
{
    public struct VoxelBuildConfig
    {
        public float cellSize; //体素大小
        public float cellHeight; //体素高度
        public float[] bmin;
        public float[] bmax;
        public float walkableSlopeAngle;
        public int walkableHeight;
        public int walkableClimb;
        public int walkableRadius;
        public int waterThresold;
        public int width;
        public int height;
    }

    public enum VoxelMask
    {
        MaskWater = 1 << 0,
        MaskWaterBottom = 1 << 1
    }


    public class VoxelTool
    {
        private readonly Dictionary<ushort, MeshData> _meshDict;
        private VoxelBuildConfig _buildConfig;

        private readonly BuildContext _ctx;

        private byte[] _triAreas = null;
        private Recast.rcHeightfield _solid = null;
        private Recast.rcCompactHeightfield _chf = null;
        private Recast.rcPolyMesh _polyMesh = null;
        private Recast.rcPolyMeshDetail _polyMeshDetail = null;
        private byte[] _navMeshData = null;
        private int _navMeshDataSize = 0;
        bool _buildSuccess = false;

        bool _filterLowHangingObstacles = true;
        bool _filterLedgeSpans = true;
        bool _filterWalkableLowHeightSpans = true;

        // 为了防止体素被剔除，构建体素时采用一套通用的参数
        private const int AgentRadius = 0;
        private const int ClimbHeight = 9999999;
        private const int MaxSlope = 89;
        private const int SpanElementNum = 4; // min，max，mask,area

        // MapVoxel mVoxel = null;
        // dtMapVoxel mDtVoxel = null;

        public bool buildSuccess => _buildSuccess;

        public VoxelTool()
        {
            _meshDict = new();
            _buildConfig = new VoxelBuildConfig();
            _buildConfig.bmin = new float[3];
            _buildConfig.bmax = new float[3];
            _ctx = new BuildContext();
        }

        static bool IsWaterMask(ushort mask)
        {
            return (mask & (ushort)VoxelMask.MaskWater) != 0;
        }

        static bool IsWaterSpan(Recast.rcSpan s)
        {
            return s != null && (s.mask & (ushort)VoxelMask.MaskWater) != 0;
        }

        public void Reset()
        {
            _meshDict.Clear();
            Cleanup();
        }


        public void SetBuildConfig(float cellSize, float cellHeight, float agentHeight, float angentClimbHeight,
            float agentRadius, float agentMaxSlope)
        {
            _buildConfig.cellSize = cellSize;
            _buildConfig.cellHeight = cellHeight;
            _buildConfig.walkableSlopeAngle = agentMaxSlope;
            _buildConfig.walkableHeight = Mathf.CeilToInt(agentHeight / cellHeight);
            _buildConfig.waterThresold = (int)(_buildConfig.walkableHeight * 0.8f);
            _buildConfig.walkableClimb = Mathf.FloorToInt(angentClimbHeight / cellHeight);
            _buildConfig.walkableRadius = Mathf.CeilToInt(agentRadius / cellSize);
        }

        public void SetBoundBox(float minx, float miny, float minz, float maxx, float maxy, float maxz)
        {
            _buildConfig.bmin[0] = minx;
            _buildConfig.bmin[1] = miny;
            _buildConfig.bmin[2] = minz;
            _buildConfig.bmax[0] = maxx;
            _buildConfig.bmax[1] = maxy;
            _buildConfig.bmax[2] = maxz;
        }

        public bool filterLowHangingObstacles
        {
            set => _filterLowHangingObstacles = value;
        }

        public bool filterLedgeSpans
        {
            set => _filterLedgeSpans = value;
        }

        public bool filterWalkableLowHeightSpans
        {
            set => _filterWalkableLowHeightSpans = value;
        }

        /// <summary>
        /// 添加网格数据
        /// </summary>
        /// <param name="vertexes"></param>
        /// <param name="vertexNum"></param>
        /// <param name="triangles"></param>
        /// <param name="triangleNum"></param>
        /// <param name="area"></param>
        /// <param name="mask"></param>
        /// <returns></returns>
        public bool AddMesh(float[] vertexes, int vertexNum, int[] triangles, int triangleNum, byte area, ushort mask)
        {
            if (!_meshDict.TryGetValue(mask, out var meshData))
            {
                meshData = new MeshData();
                _meshDict.Add(mask, meshData);
            }

            return meshData.AddMesh(vertexes, vertexNum, triangles, triangleNum, area, mask);
        }

        public bool BuildVoxel()
        {
            if (!_meshDict.TryGetValue(0, out var meshData))
            {
                _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR, "[VoxelTool::Build]: 找不到基础地形(mask=0).");
                return false;
            }

            //计算格子数量
            Recast.rcCalcGridSize(_buildConfig.bmin, _buildConfig.bmax, _buildConfig.cellSize, out _buildConfig.width,
                out _buildConfig.height);
            _ctx.resetTimers();
            _ctx.log(Recast.rcLogCategory.RC_LOG_PROGRESS,
                $"Building voxel: {_buildConfig.width} x {_buildConfig.height} cells ,{_buildConfig.bmin[0]},{_buildConfig.bmin[1]},{_buildConfig.bmin[2]},{_buildConfig.bmax[0]},{_buildConfig.bmax[1]},{_buildConfig.bmax[2]}");
            _ctx.startTimer(Recast.rcTimerLabel.RC_TIMER_TOTAL);

            _solid = new Recast.rcHeightfield();
            if (!Recast.rcCreateHeightfield(_ctx, _solid, _buildConfig.width, _buildConfig.height, _buildConfig.bmin,
                    _buildConfig.bmax, _buildConfig.cellSize, _buildConfig.cellHeight))
            {
                _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR, "[VoxelTool::Build]: 创建高度场失败.");
                return false;
            }

            _ctx.log(Recast.rcLogCategory.RC_LOG_PROGRESS,
                $"mask : 0, {meshData.vertexNum} vertices, {meshData.triangleNum} triangles");

            _triAreas = new byte[meshData.triangleNum];

            // Find triangles which are walkable based on their slope and rasterize them.
            // If your input data is multiple meshes, you can transform them here, calculate
            // the are type for each of the meshes and rasterize them.
            Array.Clear(_triAreas, 0, meshData.triangleNum);
            // 根据三角面坡度确定不可行走区域
            Recast.rcMarkWalkableTriangles(_ctx, MaxSlope, meshData.vertexes, meshData.vertexNum,
                meshData.triangles, meshData.triangleNum, _triAreas);
            // 光栅化三角面，用walkableclimb进行上下合并
            if (!Recast.rcRasterizeTriangles(_ctx, meshData.vertexes, meshData.vertexNum, meshData.triangles, _triAreas,
                    meshData.masks, meshData.triangleNum, _solid, ClimbHeight))
            {
                _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR, "[VoxelTool::Build]: RasterizeTriangles失败.");
                return false;
            }

            _triAreas = null;

            // 若一个span标记为可行走，那么位于它上方且高度相差小于walkableClimb的span也应该标记为可行走。这一步会扩大可行走区域判定。
            if (_filterLowHangingObstacles)
                Recast.rcFilterLowHangingWalkableObstacles(_ctx, ClimbHeight, _solid);
            // 过滤突起span，如果从span顶端到相邻区间下降距离超过了WalkableClimb，那么这个span被认为是个突起，不可行走。这里的相邻区域定义为前后左右四个方向。
            if (_filterLedgeSpans)
                Recast.rcFilterLedgeSpans(_ctx, _buildConfig.walkableHeight, ClimbHeight, _solid);
            // 过滤可行走的低高度span。当span上方有距离小于walkableHeight的障碍物时，它的顶端表面也不可行走，因为容纳的高度不够
            if (_filterWalkableLowHeightSpans)
                Recast.rcFilterWalkableLowHeightSpans(_ctx, _buildConfig.walkableHeight, _solid);
            // 合并不可行走的体素(area为null)
            FillNullSpans();
            
            // TODO: 此处放到编辑器检查
            // 若某个cell没有span，则判断为不可行走，自动填充一个最大的span
            // FillEmptyCells();


            _chf = new Recast.rcCompactHeightfield();
            // 构建heighfield，包含neighbours信息，加速后期处理
            if (!Recast.rcBuildCompactHeightfield(_ctx, _buildConfig.walkableHeight, ClimbHeight, _solid, _chf))
            {
                _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR, "buildNavigation: Could not build compact data.");
                return false;
            }

            // Erode the walkable area by agent radius.
            // 把距离边缘walkableRadius内的span设置为不可行走
            if (!Recast.rcErodeWalkableArea(_ctx, AgentRadius, _chf))
            {
                _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR, "buildNavigation: Could not erode.");
                return false;
            }

            if (!OptimizeSolidHeightField())
            {
                _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR, "buildNavigation: Optimize solid height field failed.");
                return false;
            }

            _chf = null;
            // 在地图边界填充一圈体素，防止玩家移动时超出范围
            // FillBoundaryCells();

            // 光栅化其他mask的mesh
            foreach (KeyValuePair<ushort, MeshData> iter in _meshDict)
            {
                if (iter.Key == 0)
                    continue;
                MeshData data = iter.Value;
                _ctx.log(Recast.rcLogCategory.RC_LOG_PROGRESS,
                    $"光栅化 mask: {iter.Key} - {data.vertexNum} verts, {data.triangleNum} tris");
                _triAreas = new byte[data.triangleNum];
                if (_triAreas == null)
                {
                    _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR,
                        $"[VoxelTool.Build]: Out of memory 'm_triareas' ({data.triangleNum}) {iter.Key}");
                    return false;
                }

                Array.Clear(_triAreas, 0, data.triangleNum);
                Recast.rcMarkWalkableTriangles(_ctx, MaxSlope, data.vertexes, data.vertexNum, data.triangles,
                    data.triangleNum, _triAreas);
                if (!Recast.rcRasterizeTriangles(_ctx, data.vertexes, data.vertexNum, data.triangles, _triAreas,
                        data.masks, data.triangleNum, _solid, ClimbHeight))
                {
                    _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR, "[VoxelTool.Build]: Could not rasterize triangles.");
                    return false;
                }

                _triAreas = null;
            }

            // 减少体素数量
            // RemoveBottomWaterSpans();
            // 填充上下容量不足以容纳agent的体素
            FillWaterSpans();

            // 1、扩展水体体素至水底
            // 2、标记水底的体素
            ExtendWaterSpans(_buildConfig.waterThresold);
            // 计算各个体素与相邻体素的连通性
            // CalcConnections(agentHeight, climbHeight, waterThresold);
            _ctx.stopTimer(Recast.rcTimerLabel.RC_TIMER_TOTAL);
            _ctx.log(Recast.rcLogCategory.RC_LOG_PROGRESS,
                $"build time: {_ctx.getAccumulatedTime(Recast.rcTimerLabel.RC_TIMER_TOTAL) * 0.001f}");

            _buildSuccess = true;

            return true;
        }

        struct AddSpanInfo
        {
            public int x;
            public int y;
            public ushort smin;
            public ushort smax;
            public byte area;
            public ushort mask;
        }

        private void FillNullSpans()
        {
            List<AddSpanInfo> addSpanList = new List<AddSpanInfo>();

            int w = _solid.width;
            int h = _solid.height;
            const int MAX_HEIGHT = 0xffff;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    for (Recast.rcSpan s = _solid.spans[x + y * w]; s != null; s = s.next)
                    {
                        if (s.area == Recast.RC_NULL_AREA && s.next != null)
                        {
                            AddSpanInfo info = new AddSpanInfo
                            {
                                x = x,
                                y = y,
                                smin = s.smax,
                                smax = s.next?.smin ?? MAX_HEIGHT,
                                area = s.area,
                                mask = s.next?.mask ?? 0
                            };
                            addSpanList.Add(info);
                        }
                    }
                }
            }

            foreach (AddSpanInfo info in addSpanList)
            {
                Recast.rcAddSpan(_ctx, _solid, info.x, info.y, info.smin, info.smax, info.area,
                    _buildConfig.walkableClimb,
                    info.mask);
            }
        }

        private void FillEmptyCells()
        {
            List<AddSpanInfo> addSpanList = new List<AddSpanInfo>();
            int w = _solid.width;
            int h = _solid.height;
            const int MAX_HEIGHT = 0xffff;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Recast.rcSpan s = _solid.spans[x + y * w];
                    if (s == null)
                    {
                        AddSpanInfo info = new AddSpanInfo
                        {
                            x = x,
                            y = y,
                            smin = 0,
                            smax = MAX_HEIGHT,
                            area = Recast.RC_NULL_AREA,
                            mask = 0
                        };
                        addSpanList.Add(info);
                    }
                }
            }

            foreach (AddSpanInfo info in addSpanList)
            {
                Recast.rcAddSpan(_ctx, _solid, info.x, info.y, info.smin, info.smax, info.area,
                    _buildConfig.walkableClimb,
                    info.mask);
            }
        }

        /// <summary>
        /// 优化实体高度场，标记可连通的区域，将封闭空间作为阻挡实体处理，重新加入到SolidHeightField中
        /// </summary>
        /// <returns></returns>
        private bool OptimizeSolidHeightField()
        {
            //TODO: 优化solid
            return true;
        }


        private void FillBoundaryCells()
        {
            int w = _solid.width;
            int h = _solid.height;

            for (int x = 0; x < w; x++)
            {
                Recast.rcAddSpan(_ctx, _solid, x, 0, 0, 0xffff, 0, _buildConfig.walkableClimb, 0);
                Recast.rcAddSpan(_ctx, _solid, x, h - 1, 0, 0xffff, 0, _buildConfig.walkableClimb, 0);
            }

            for (int y = 0; y < h; y++)
            {
                Recast.rcAddSpan(_ctx, _solid, 0, y, 0, 0xffff, 0, _buildConfig.walkableClimb, 0);
                Recast.rcAddSpan(_ctx, _solid, w - 1, y, 0, 0xffff, 0, _buildConfig.walkableClimb, 0);
            }
        }

        private void FillWaterSpans()
        {
            int w = _solid.width;
            int h = _solid.height;
            List<AddSpanInfo> addSpanList = new List<AddSpanInfo>();

            // 填充高度容量不够的体素
            addSpanList.Clear();
            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    Recast.rcSpan s = _solid.spans[x + y * w];
                    Recast.rcSpan prev = null;
                    while (s != null)
                    {
                        if (s.next != null && !IsWaterSpan(s.next) &&
                            s.next.smin - s.smax < _buildConfig.walkableHeight)
                        {
                            ushort mask = s.mask;
                            s.mask = s.next.mask;
                            addSpanList.Add(new AddSpanInfo
                            {
                                x = x, y = y, smin = (ushort)s.smax, smax = (ushort)s.next.smax,
                                area = Recast.RC_NULL_AREA,
                                mask = s.next.mask
                            });

                            // 对于水面或者浅滩的体素，由于上表面已经被填充掉了，下表面也没有存留的必要，也一起填充掉
                            if (IsWaterMask(mask) && prev != null)
                            {
                                prev.mask = s.next.mask;
                                addSpanList.Add(new AddSpanInfo
                                {
                                    x = x, y = y, smin = (ushort)prev.smax, smax = (ushort)s.smax,
                                    area = Recast.RC_NULL_AREA,
                                    mask = s.next.mask
                                });
                            }
                        }

                        prev = s;
                        s = s.next;
                    }
                }
            }

            if (addSpanList.Count > 0)
            {
                foreach (AddSpanInfo info in addSpanList)
                {
                    Recast.rcAddSpan(_ctx, _solid, info.x, info.y, info.smin, info.smax, info.area,
                        _buildConfig.walkableClimb, info.mask);
                }
            }
        }

        public void RemoveBottomWaterSpans()
        {
            int w = _solid.width;
            int h = _solid.height;
            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    Recast.rcSpan s = _solid.spans[x + y * w];
                    //TODO:这里应该使用while？
                    if (s != null && IsWaterSpan(s))
                    {
                        _solid.spans[x + y * w] = s.next;
                        Recast.freeSpan(_solid, s);
                    }
                }
            }
        }

        private void ExtendWaterSpans(int waterThreshold)
        {
            int w = _solid.width;
            int h = _solid.height;
            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    Recast.rcSpan prev = null;
                    Recast.rcSpan s = _solid.spans[x + y * w];
                    while (s != null)
                    {
                        if (IsWaterSpan(s) && prev != null && !IsWaterSpan(prev))
                        {
                            s.smin = prev.smax;
                            prev.mask |= (ushort)VoxelMask.MaskWaterBottom;
                        }

                        prev = s;
                        s = s.next;
                    }
                }
            }
        }

        public int GetSpans(int x, int z, ushort[] buffer, uint maxNum)
        {
            if (!_buildSuccess)
                return -1;
            if (x < 0 || x >= _solid.width || z < 0 || z >= _solid.height)
            {
                return -1;
            }

            uint counter = 0;
            Recast.rcSpan span = _solid.spans[x + z * _solid.width];
            while (span != null && counter < maxNum)
            {
                if (span.area != Recast.RC_NULL_AREA)
                {
                    buffer[counter * SpanElementNum] = span.smin;
                    buffer[counter * SpanElementNum + 1] = span.smax;
                    buffer[counter * SpanElementNum + 2] = span.mask;
                    ++counter;
                }

                span = span.next;
            }

            return (int)counter;
        }


        private void Cleanup()
        {
            _triAreas = null;
            _solid = null;
            _chf = null;
            _polyMesh = null;
            _polyMeshDetail = null;
            _navMeshData = null;
            _buildSuccess = false;
        }
    }
}