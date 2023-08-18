using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace RecastSharp
{
    /// <summary>
    /// 体素构建配置
    /// </summary>
    public struct VoxelBuildConfig
    {
        public float CellSize; //体素大小
        public float CellHeight; //体素高度
        public float[] Min;
        public float[] Max;
        public float WalkableSlopeAngle;
        public int WalkableHeight;
        public int WalkableClimb;
        public int WalkableRadius;
        public int Width;
        public int Height;
    }

    /// <summary>
    /// 用于填充的Span数据结构
    /// </summary>
    struct FillSpanInfo
    {
        public int X;
        public int Y;
        public ushort Min;
        public ushort Max;
        public byte Area;
    }

    /// <summary>
    /// 反体素结构
    /// </summary>
    public class AntiSpan
    {
        public ushort Min;
        public ushort Max;
        public byte Flag;
        public AntiSpan Next;

        public AntiSpan Link;
    }

    public class UnionAntiSpan
    {
        public ushort Min;

        // 并查集的代表元素，初始为自身
        private UnionAntiSpan _link;

        public UnionAntiSpan FindRoot()
        {
            if (_link == null)
            {
                return this;
            }

            // 路径压缩，将该节点的 Link 直接指向根节点
            _link = _link.FindRoot();
            return _link;
        }

        // 合并两个集合
        public void Union(UnionAntiSpan other)
        {
            UnionAntiSpan rootA = FindRoot();
            UnionAntiSpan rootB = other.FindRoot();

            // 将一个集合的根节点指向另一个集合的根节点
            if (rootA != rootB)
            {
                rootA._link = rootB;
            }
        }

        public bool AreConnected(UnionAntiSpan other)
        {
            return FindRoot() == other.FindRoot();
        }
    }

    /// <summary>
    /// 体素构建网格缓存数据（缓存VoxelBuildTool的CollectMeshInfo）
    /// </summary>
    public class BuildMeshInfo
    {
        public string MeshRootName;
        public bool HasLod;
    }

    public class VoxelTool
    {
        private MeshData _meshData;
        private VoxelBuildConfig _buildConfig;

        private readonly BuildContext _ctx;

        private byte[] _triAreas;
        private Recast.rcHeightfield _solid;
        private Recast.rcCompactHeightfield _chf;
        bool _buildVoxelSuccess;
        bool _mergeSpanSuccess;

        bool _filterLowHangingObstacles = true;
        bool _filterLedgeSpans = true;
        bool _filterWalkableLowHeightSpans = true;
        Vector3Int _walkablePoint;

        public const int SpanElementNum = 3; // min，max，area

        private List<BuildMeshInfo> _buildInfos;

        public bool buildVoxelSuccess => _buildVoxelSuccess;

        public VoxelTool()
        {
            _meshData = new MeshData();
            _buildConfig = new VoxelBuildConfig();
            _buildConfig.Min = new float[3];
            _buildConfig.Max = new float[3];
            _ctx = new BuildContext();
            _buildInfos = new List<BuildMeshInfo>(10);
        }

        public void Reset()
        {
            _meshData.Release();
            Cleanup();
        }


        public void SetBuildConfig(float cellSize, float cellHeight, float agentHeight, float agentClimbHeight,
            float agentRadius, float agentMaxSlope)
        {
            _buildConfig.CellSize = cellSize;
            _buildConfig.CellHeight = cellHeight;
            _buildConfig.WalkableSlopeAngle = agentMaxSlope;
            _buildConfig.WalkableHeight = Mathf.CeilToInt(agentHeight / cellHeight);
            _buildConfig.WalkableClimb = Mathf.FloorToInt(agentClimbHeight / cellHeight);
            _buildConfig.WalkableRadius = Mathf.CeilToInt(agentRadius / cellSize);
        }

        public void SetBoundBox(float minx, float miny, float minz, float maxx, float maxy, float maxz)
        {
            _buildConfig.Min[0] = minx;
            _buildConfig.Min[1] = miny;
            _buildConfig.Min[2] = minz;
            _buildConfig.Max[0] = maxx;
            _buildConfig.Max[1] = maxy;
            _buildConfig.Max[2] = maxz;
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

        public Vector3Int point
        {
            set => _walkablePoint = value;
        }

        public bool CollectVoxelMesh(List<CollectMeshInfo> collectMeshInfos)
        {
            _buildInfos.Clear();
            if (collectMeshInfos != null)
            {
                foreach (var info in collectMeshInfos)
                {
                    if (info.target == null)
                        continue;
                    AddBuildVoxelMeshRoot(info.target, info.hasLod);
                }
            }

            if (_meshData.vertexNum == 0)
            {
                EditorUtility.DisplayDialog("错误", "请设置正确的体素处理网格对象,当前构造体素网格数据为空！！", "关闭");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 添加某一个场景节点下的所有网格，用于构造体素
        /// </summary>
        /// <param name="root">场景GameObject节点</param>
        /// <param name="hasLod">节点下的网格对象是否有LOD</param>
        private void AddBuildVoxelMeshRoot(GameObject root, bool hasLod)
        {
            _buildInfos.Add(new BuildMeshInfo { MeshRootName = root.name, HasLod = hasLod });
            if (hasLod)
            {
                LODGroup[] lodGroups = root.GetComponentsInChildren<LODGroup>();
                foreach (LODGroup group in lodGroups)
                {
                    LOD lod = group.GetLODs()[0];
                    foreach (Renderer r in lod.renderers)
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        AddVoxelMesh(mf);
                    }
                }
            }
            else
            {
                MeshFilter[] mfs = root.GetComponentsInChildren<MeshFilter>();
                foreach (MeshFilter meshFilter in mfs)
                {
                    AddVoxelMesh(meshFilter);
                }
            }
        }

        /// <summary>
        /// 添加单个网格构造体素
        /// </summary>
        /// <param name="meshFilter"></param>
        private void AddVoxelMesh(MeshFilter meshFilter)
        {
            if (meshFilter.gameObject.activeSelf && meshFilter.gameObject.activeInHierarchy &&
                meshFilter.sharedMesh != null)
            {
                Mesh mesh = meshFilter.sharedMesh;
                Vector3[] meshVertices = mesh.vertices;
                int vertexCount = meshVertices.Length;
                float[] vertices = new float[vertexCount * 3];
                for (int i = 0; i < vertexCount; i++)
                {
                    Vector3 globalVertex = meshFilter.transform.TransformPoint(meshVertices[i]);
                    vertices[i * 3] = globalVertex.x;
                    vertices[i * 3 + 1] = globalVertex.y;
                    vertices[i * 3 + 2] = globalVertex.z;
                }

                _meshData.AddMesh(vertices, vertexCount, mesh.triangles, mesh.triangles.Length / 3, 0);
            }
        }


        /// <summary>
        /// 构造体素
        /// </summary>
        /// <returns></returns>
        public bool BuildVoxel()
        {
            //计算格子数量
            Recast.rcCalcGridSize(_buildConfig.Min, _buildConfig.Max, _buildConfig.CellSize, out _buildConfig.Width,
                out _buildConfig.Height);

            // Reset build times gathering.
            _ctx.resetTimers();

            _ctx.log(Recast.rcLogCategory.RC_LOG_PROGRESS,
                $"Building voxel: {_buildConfig.Width} x {_buildConfig.Height} cells ,{_buildConfig.Min[0]},{_buildConfig.Min[1]},{_buildConfig.Min[2]},{_buildConfig.Max[0]},{_buildConfig.Max[1]},{_buildConfig.Max[2]}");

            // Start the build process.	
            _ctx.startTimer(Recast.rcTimerLabel.RC_TIMER_TOTAL);

            //
            // Step 2. Rasterize input polygon soup.
            //

            // Allocate voxel heightfield where we rasterize our input data to.
            _solid = new Recast.rcHeightfield();
            if (!Recast.rcCreateHeightfield(_ctx, _solid, _buildConfig.Width, _buildConfig.Height, _buildConfig.Min,
                    _buildConfig.Max, _buildConfig.CellSize, _buildConfig.CellHeight))
            {
                _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR, "[VoxelTool::Build]: Could not create solid heightfield.");
                return false;
            }

            _ctx.log(Recast.rcLogCategory.RC_LOG_PROGRESS,
                $"mask : 0, {_meshData.vertexNum} vertices, {_meshData.triangleNum} triangles");

            // Allocate array that can hold triangle area types.
            // If you have multiple meshes you need to process, allocate
            // and array which can hold the max number of triangles you need to process.
            _triAreas = new byte[_meshData.triangleNum];

            // Find triangles which are walkable based on their slope and rasterize them.
            // If your input data is multiple meshes, you can transform them here, calculate
            // the are type for each of the meshes and rasterize them.

            // 根据三角面坡度确定不可行走区域
            Recast.rcMarkWalkableTriangles(_ctx, _buildConfig.WalkableSlopeAngle, _meshData.vertices,
                _meshData.vertexNum,
                _meshData.triangles, _meshData.triangleNum, _triAreas);
            // 光栅化三角面，用walkableclimb进行上下合并
            if (!Recast.rcRasterizeTriangles(_ctx, _meshData.vertices, _meshData.vertexNum, _meshData.triangles,
                    _triAreas,
                    _meshData.triangleNum, _solid, _buildConfig.WalkableClimb))
            {
                _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR, "[VoxelTool::Build]: Could not rasterize triangles.");
                return false;
            }

            _triAreas = null;

            //
            // Step 3. Filter walkables surfaces.
            //
            FilterHeightField();
            // 合并不可行走的体素(area为null)
            FillNullSpans();
            //将体素的第一个span的下表面改为0
            FillFirstSpans();
            //合并封闭空间的网格
            MergeClosedSpaceVoxel();

            //
            // Step 4. Partition walkable surface to simple regions.
            //
            _chf = new Recast.rcCompactHeightfield();
            // 构建CompactHeightfield 紧缩高度场，包含neighbours信息，加速后期处理
            if (!Recast.rcBuildCompactHeightfield(_ctx, _buildConfig.WalkableHeight, _buildConfig.WalkableClimb, _solid,
                    _chf))
            {
                _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR, "buildNavigation: Could not build compact data.");
                return false;
            }

            // Erode the walkable area by agent radius.
            // 构建agentRadius区域，把距离边缘walkableRadius内的span设置为不可行走
            if (!Recast.rcErodeWalkableArea(_ctx, _buildConfig.WalkableRadius, _chf))
            {
                _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR, "buildNavigation: Could not erode.");
                return false;
            }


            _ctx.stopTimer(Recast.rcTimerLabel.RC_TIMER_TOTAL);
            long milliseconds = _ctx.getAccumulatedTime(Recast.rcTimerLabel.RC_TIMER_TOTAL) /
                                TimeSpan.TicksPerMillisecond;
            TimeSpan timeSpan = TimeSpan.FromMilliseconds(milliseconds);
            _ctx.log(Recast.rcLogCategory.RC_LOG_PROGRESS,
                $"build time: {timeSpan:mm\\:ss\\.fff}");

            _buildVoxelSuccess = true;

            return true;
        }


        /// <summary>
        /// 过滤操作
        /// </summary>
        private void FilterHeightField()
        {
            // 若一个span标记为可行走，那么位于它上方且高度相差小于walkableClimb的span也应该标记为可行走。这一步会扩大可行走区域判定。
            //上下两个span，下span可走，上span不可走，并且上下span的上表面相差不超过walkClimb，则把上span也改为可走
            if (_filterLowHangingObstacles)
                Recast.rcFilterLowHangingWalkableObstacles(_ctx, _buildConfig.WalkableClimb, _solid);
            // 过滤突起span，如果从span顶端到相邻区间下降距离超过了WalkableClimb，那么这个span被认为是个突起，不可行走。这里的相邻区域定义为前后左右四个方向。
            // span比某个邻居span的上表面高出walkClimb，说明从span到邻居span有一个落差，则把span标记为不可行走。
            // span与其他邻居上表面相比都在walkClimb之内，但是邻居span之间的上下表面高度差超过walkClimb，说明span处于比较陡峭的地方，则把span标记为不可行走。
            if (_filterLedgeSpans)
                Recast.rcFilterLedgeSpans(_ctx, _buildConfig.WalkableHeight, _buildConfig.WalkableClimb, _solid);
            // 过滤可行走的低高度span。当span上方有距离小于walkableHeight的障碍物时，它的顶端表面也不可行走，因为容纳的高度不够
            //如果上下两个span之间的空隙小于等于walkHeight，则把下span标记为不可行走。
            if (_filterWalkableLowHeightSpans)
                Recast.rcFilterWalkableLowHeightSpans(_ctx, _buildConfig.WalkableHeight, _solid);
        }


        /// <summary>
        /// 填充不可行走面的体素
        /// </summary>
        private void FillNullSpans()
        {
            List<FillSpanInfo> addSpanList = new List<FillSpanInfo>();

            int w = _solid.width;
            int h = _solid.height;
            ushort maxHeight = (ushort)(Mathf.CeilToInt(_solid.bmax[1] / _solid.ch) - 1); //最大飞行高度

            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (Recast.rcSpan s = _solid.spans[x + y * w]; s != null; s = s.next)
                    {
                        if (s.area == Recast.RC_NULL_AREA && s.next != null)
                        {
                            FillSpanInfo info = new FillSpanInfo
                            {
                                X = x,
                                Y = y,
                                Min = s.smax,
                                Max = s.next?.smin ?? maxHeight,
                                Area = s.area,
                            };
                            addSpanList.Add(info);
                        }
                    }
                }
            }

            foreach (var info in addSpanList)
            {
                Recast.rcAddSpan(_ctx, _solid, info.X, info.Y, info.Min, info.Max, info.Area,
                    _buildConfig.WalkableClimb);
            }
        }

        //将每一个体素格子的第一个span的下表面改为0
        private void FillFirstSpans()
        {
            int w = _solid.width;
            int h = _solid.height;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Recast.rcSpan s = _solid.spans[x + y * w];
                    if (s != null)
                    {
                        s.smin = 0;
                    }
                }
            }
        }


        public uint GetSpans(int x, int z, ushort[] buffer, uint maxNum)
        {
            if (!_buildVoxelSuccess)
            {
                return 0;
            }

            if (x < 0 || x >= _solid.width || z < 0 || z >= _solid.height)
            {
                return 0;
            }

            uint count = 0;
            Recast.rcSpan span = _solid.spans[x + z * _solid.width];
            while (span != null && count < maxNum)
            {
                {
                    buffer[count * SpanElementNum] = span.smin;
                    buffer[count * SpanElementNum + 1] = span.smax;
                    buffer[count * SpanElementNum + 2] = span.area;
                    ++count;
                }

                span = span.next;
            }

            if (count > maxNum)
            {
                Debug.LogErrorFormat("x:{0},z:{1} span count is out of max!!!!!counter:{2},maxNum:{3}", x, z, count,
                    maxNum);
            }

            return count;
        }

        private void Cleanup()
        {
            _triAreas = null;
            _solid = null;
            _chf = null;
            _buildVoxelSuccess = false;
            _mergeSpanSuccess = false;
        }

        public void TestOffset(int cellX, int cellY)
        {
            Recast.rcSpan span = _solid.spans[cellX + cellY * _solid.width];
            float raycastYOffset = _buildConfig.WalkableClimb * 0.5f * _solid.ch;
            while (span != null)
            {
                double topHeight = span.smax * _solid.ch;
                float raycastY = (float)(topHeight + raycastYOffset);
                float realHeight = GetVoxelSpanRealHeight(cellX, cellY, _solid.cs, raycastY);
                byte offset = 0;
                if (realHeight != 0)
                {
                    int offsetInt = (int)(span.smax - realHeight / _solid.ch);
                    offsetInt = Math.Max(Byte.MinValue, Math.Min(offsetInt, MaxHeightOffset));
                    offset = (byte)offsetInt;
                }

                Debug.LogFormat("realHeight:{0},topHeight:{1},span.smax:{2},offset:{3},offset_byte:{4}", realHeight,
                    topHeight, span.smax,
                    span.smax - realHeight / _solid.ch, offset);
                CreateCube((cellX + 0.5f) * _solid.cs, (span.smax - MaxHeightOffset) * _solid.ch,
                    (cellY + 0.5f) * _solid.cs, "result");
                span = span.next;
            }
        }

        public void CreateCube(float x, float y, float z, string name)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.position = new Vector3(x, y, z);
            cube.transform.localScale = new Vector3(0.5f, 0, 0.5f);
        }

        public bool SaveFullJsonData(string dirPath, int regionSize)
        {
            if (_solid == null)
            {
                return false;
            }

            JsonMapVoxel mapVoxel = new JsonMapVoxel();
            mapVoxel.Init(_solid, regionSize);
            //计算射线的起始高度直接抬高WalkableClimb的一半
            float raycastYOffset = _buildConfig.WalkableClimb * 0.5f * mapVoxel.voxelHeight;
            // 分区域分析并填充数据
            for (int i = 0; i < mapVoxel.regionNum; i++)
            {
                int cx = i % mapVoxel.regionWidth * regionSize;
                int cy = i / mapVoxel.regionWidth * regionSize;
                JsonRegionVoxelData region = new JsonRegionVoxelData();
                mapVoxel.regions[i] = region;
                region.cellWidth = (ushort)(cx + regionSize <= _solid.width ? regionSize : _solid.width - cx);
                region.cellHeight = (ushort)(cy + regionSize <= _solid.height ? regionSize : _solid.height - cy);

                int cellCount = region.cellHeight * region.cellWidth;
                region.spans = new CellSpanInfo[cellCount];
                int index = 0;
                for (int y = 0; y < region.cellHeight; ++y)
                {
                    var cellY = y + cy;
                    for (int x = 0; x < region.cellWidth; ++x)
                    {
                        var cellX = x + cx;
                        int spNum = 0;

                        Recast.rcSpan span = _solid.spans[cellX + cellY * _solid.width];
                        Recast.rcSpan firstSpan = span;
                        while (span != null)
                        {
                            ++spNum;
                            span = span.next;
                        }

                        CellSpanInfo info = new CellSpanInfo(cellX, cellY, spNum);
                        if (spNum > 0)
                        {
                            for (int j = 0; j < spNum; j++)
                            {
                                double topHeight = firstSpan.smax * mapVoxel.voxelHeight;
                                float raycastY = (float)(topHeight + raycastYOffset);
                                float realHeight =
                                    GetVoxelSpanRealHeight(cellX, cellY, mapVoxel.voxelSize, raycastY);
                                byte offset = 0;
                                //realHeight==0表示，5个点，都拿不到高度或者就是贴着Y轴0点，这种情况下，不需要偏移
                                //TODO:是否需要考虑MaxHeightOffset > span.max - span.min???
                                if (realHeight != 0)
                                {
                                    int offsetInt = (int)(firstSpan.smax - realHeight / _solid.ch);
                                    offsetInt = Math.Max(Byte.MinValue, Math.Min(offsetInt, MaxHeightOffset));
                                    offset = (byte)offsetInt;
                                }

                                info.spans[j] = new VoxelSpan(firstSpan.smin, firstSpan.smax, firstSpan.area, offset);
                                firstSpan = firstSpan.next;
                            }
                        }

                        region.spans[index] = info;
                        index++;
                    }
                }


                File.WriteAllText(Path.Combine(dirPath, $"region_{i}_full.json"),
                    JsonConvert.SerializeObject(region, Formatting.Indented));
            }

            return true;
        }

        public bool SaveClientData(string binDirPath, string jsonDirPath, int regionSize)
        {
            if (_solid == null)
            {
                return false;
            }

            ClientMapVoxel clientMapVoxel = new ClientMapVoxel();
            clientMapVoxel.Init(_solid, regionSize);
            // 分区域分析并填充数据
            VoxelSpan[][][] cellSpans = new VoxelSpan[clientMapVoxel.regionNum][][];

            //计算射线的起始高度直接抬高WalkableClimb的一半
            float raycastYOffset = _buildConfig.WalkableClimb * 0.5f * clientMapVoxel.voxelHeight;

            for (int i = 0; i < clientMapVoxel.regionNum; i++)
            {
                // 计算区域的起始坐标，注意了，这里相当于原始坐标-offset
                int cx = i % clientMapVoxel.regionWidth * regionSize;
                int cy = i / clientMapVoxel.regionHeight * regionSize;
                RegionVoxelData region = new RegionVoxelData
                {
                    index = i
                };
                clientMapVoxel.regions[i] = region;
                region.cellWidthNum = (ushort)(cx + regionSize <= _solid.width ? regionSize : _solid.width - cx);
                region.cellHeightNum = (ushort)(cy + regionSize <= _solid.height ? regionSize : _solid.height - cy);

                int cellCount = region.cellHeightNum * region.cellWidthNum;
                VoxelSpan[][] regionCellSpans = new VoxelSpan[cellCount][];
                cellSpans[i] = regionCellSpans;

                int totalSpanNum = 0;
                uint tmpCellIdx = 0;
                byte offset;
                for (int y = 0; y < region.cellHeightNum; ++y)
                {
                    var cellY = y + cy;
                    for (int x = 0; x < region.cellWidthNum; ++x)
                    {
                        var cellX = x + cx;
                        byte spNum = 0;
                        int realIndex = cellX + cellY * _solid.width;
                        for (Recast.rcSpan s = _solid.spans[realIndex]; s != null; s = s.next)
                        {
                            spNum++;
                            totalSpanNum++;
                        }

                        if (spNum > 0)
                        {
                            VoxelSpan[] spans = new VoxelSpan[spNum];
                            int tmpSpanIdx = 0;
                            for (Recast.rcSpan span = _solid.spans[realIndex]; span != null; span = span.next)
                            {
                                float topHeight = span.smax * clientMapVoxel.voxelHeight;
                                float raycastY = topHeight + raycastYOffset;
                                float realHeight =
                                    GetVoxelSpanRealHeight(cellX, cellY, clientMapVoxel.voxelSize, raycastY);
                                offset = 0;
                                //realHeight==0表示，5个点，都拿不到高度或者就是贴着Y轴0点，这种情况下，不需要偏移
                                if (realHeight != 0)
                                {
                                    int offsetInt = (int)(span.smax - realHeight / _solid.ch);
                                    offsetInt = Math.Max(Byte.MinValue, Math.Min(offsetInt, MaxHeightOffset));
                                    offset = (byte)offsetInt;
                                }

                                spans[tmpSpanIdx] = new VoxelSpan(span.smin, span.smax, span.area, offset);
                                tmpSpanIdx++;
                            }

                            regionCellSpans[tmpCellIdx] = spans;
                        }

                        tmpCellIdx++;
                    }
                }

                region.totalSpanNum = totalSpanNum;
            }

            // 创建并启动多个线程，合并每一个分块的体素数据
            Task[] tasks = new Task[clientMapVoxel.regionNum];
            for (int i = 0; i < clientMapVoxel.regionNum; i++)
            {
                RegionVoxelData region = clientMapVoxel.regions[i];
                VoxelSpan[][] regionCellSpans = cellSpans[i];
                tasks[i] = Task.Run(() => MergeSpansData(region, regionCellSpans));
            }

            // 等待所有线程完成
            Task.WaitAll(tasks);
            //导出二进制
            ExportClientBytes(binDirPath, clientMapVoxel);
            //导出json对照
            ExportClientJson(jsonDirPath, clientMapVoxel);
            return true;
        }

        private static readonly RaycastHit[] Results = new RaycastHit[10];
        private readonly RaycastDistanceComparer _distanceComparer = new();

        private const byte MaxHeightOffset = 20; // 最大高度偏移量

        /// <summary>
        /// 获取体素的真实高度Offset
        /// </summary>
        /// <param name="voxelX"></param>
        /// <param name="voxelY"></param>
        /// <param name="voxelSize"></param>
        /// <param name="raycastY"></param>
        /// <returns></returns>
        private float GetVoxelSpanRealHeight(int voxelX, int voxelY, float voxelSize, float raycastY)
        {
            //初始化5个点的xz
            float left = voxelX * voxelSize;
            float right = (voxelX + 1) * voxelSize;
            float bottom = (voxelY) * voxelSize;
            float top = (voxelY + 1) * voxelSize;
            float centerX = (voxelX + 0.5f) * voxelSize;
            float centerZ = (voxelY + 0.5f) * voxelSize;

            //平均取样，如果有碰撞，取平均值
            int raycastNum = 0;
            float realHeight = 0;
            //左下
            float raycastHeight = VoxelRaycast(left, raycastY, bottom);
            if (raycastHeight > 0)
            {
                realHeight += raycastHeight;
                raycastNum++;
            }

            //左上
            raycastHeight = VoxelRaycast(left, raycastY, top);
            if (raycastHeight > 0)
            {
                realHeight += raycastHeight;
                raycastNum++;
            }

            //右下
            raycastHeight = VoxelRaycast(right, raycastY, bottom);
            if (raycastHeight > 0)
            {
                realHeight += raycastHeight;
                raycastNum++;
            }

            //右上
            raycastHeight = VoxelRaycast(right, raycastY, top);
            if (raycastHeight > 0)
            {
                realHeight += raycastHeight;
                raycastNum++;
            }

            //中心点
            raycastHeight = VoxelRaycast(centerX, raycastY, centerZ);
            if (raycastHeight > 0)
            {
                realHeight += raycastHeight;
                raycastNum++;
            }

            if (raycastNum == 0)
            {
                return 0;
            }

            return realHeight / raycastNum;
        }

        /// <summary>
        /// 获取点往下的碰撞点，用于模拟获取体素的正式高度，射线长度为5
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="z"></param>
        /// <param name="maxDistance"></param>
        /// <returns></returns>
        private float VoxelRaycast(float x, float y, float z, float maxDistance = 5)
        {
            int result = Physics.RaycastNonAlloc(new Vector3(x, y, z), Vector3.down, Results, maxDistance);
            if (result > 0)
            {
                // 按距离排序，取最近的碰撞点
                Array.Sort(Results, 0, result, _distanceComparer);
                return Results[0].point.y;
            }

            return 0f;
        }

        // 距离排序比较器
        public class RaycastDistanceComparer : IComparer<RaycastHit>
        {
            public int Compare(RaycastHit hit1, RaycastHit hit2)
            {
                return hit1.distance.CompareTo(hit2.distance);
            }
        }

        // 生成 XZ 平面对应的字符串 Key
        private string GenerateKey(VoxelSpan[] spans)
        {
            if (spans == null || spans.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder keyBuilder = new StringBuilder();

            // 将 XZ 平面上所有 VoxelSpan 的内容拼接成一个字符串
            foreach (VoxelSpan span in spans)
            {
                keyBuilder.Append(span.GetHashCode());
            }

            return keyBuilder.ToString();
        }

        /// <summary>
        /// 导出前端二进制体素数据
        /// </summary>
        /// <param name="dirPath"></param>
        /// <param name="clientMapVoxel"></param>
        private void ExportClientBytes(string dirPath, ClientMapVoxel clientMapVoxel)
        {
            // 创建并启动多个线程，每个线程处理一个 RegionVoxelData 元素
            Task[] tasks = new Task[clientMapVoxel.regionNum];
            for (int i = 0; i < clientMapVoxel.regionNum; i++)
            {
                RegionVoxelData region = clientMapVoxel.regions[i];
                string filePath = Path.Combine(dirPath, $"region_{i}.bin");
                tasks[i] = Task.Run(() => SaveRegionToBinFile(region, filePath));
            }

            // 等待所有线程完成
            Task.WaitAll(tasks);

            //前端bin文件才会打bundle
            using BinaryWriter writer =
                new BinaryWriter(File.Open(Path.Combine(dirPath, "voxel.bin"), FileMode.OpenOrCreate));
            writer.Write(clientMapVoxel.voxelSize);
            writer.Write(clientMapVoxel.voxelHeight);
            writer.Write(clientMapVoxel.mapX);
            writer.Write(clientMapVoxel.mapY);
            writer.Write(clientMapVoxel.mapZ);
            writer.Write(clientMapVoxel.regionAxisNum);
            writer.Write(clientMapVoxel.regionNum);
            writer.Write(clientMapVoxel.regionWidth);
            writer.Write(clientMapVoxel.regionHeight);
        }

        /// <summary>
        /// 合并相同的VoxelSpan数据
        /// </summary>
        /// <param name="region"></param>
        /// <param name="cellSpans"></param>
        private void MergeSpansData(RegionVoxelData region, VoxelSpan[][] cellSpans)
        {
            int cellCount = region.cellHeightNum * region.cellWidthNum;
            byte[] cellSpanCountArr = new byte[cellCount];
            uint[] cellSpanIndexArr = new uint[cellCount];
            VoxelSpan[] mergeSpans = new VoxelSpan[region.totalSpanNum];
            Dictionary<string, uint> mergedDataDict = new Dictionary<string, uint>(cellCount);
            uint tmpSpanIndex = 0;
            int mergeSpanNum = 0;
            for (int i = 0; i < cellCount; i++)
            {
                VoxelSpan[] spans = cellSpans[i];
                string key = GenerateKey(spans);
                if (string.IsNullOrEmpty(key))
                {
                    cellSpanCountArr[i] = 0;
                    cellSpanIndexArr[i] = 0;
                    continue;
                }

                cellSpanCountArr[i] = (byte)spans.Length;
                // 如果 mergedDataDict 中不存在相同的 Key，将该平面格子上的体素数据添加到 mergedDataDict 中
                if (!mergedDataDict.TryGetValue(key, out uint spanIndex))
                {
                    mergedDataDict[key] = tmpSpanIndex;
                    cellSpanIndexArr[i] = tmpSpanIndex;
                    for (int j = 0; j < spans.Length; j++)
                    {
                        mergeSpans[tmpSpanIndex + j] = spans[j];
                    }

                    tmpSpanIndex += (uint)spans.Length;
                    mergeSpanNum += spans.Length;
                }
                else
                {
                    cellSpanIndexArr[i] = spanIndex;
                }
            }

            region.cellSpanCountArr = cellSpanCountArr;
            region.cellSpanIndexArr = cellSpanIndexArr;
            region.spans = mergeSpans;
            region.mergeSpanNum = mergeSpanNum;
            Debug.LogFormat("MergeSpansData region index:{0},合并后span数量:{1},合并前span数量:{2}", region.index, mergeSpanNum,
                region.totalSpanNum);
        }

        private void SaveRegionToBinFile(RegionVoxelData region, string filePath)
        {
            //写入文件
            using BinaryWriter writer = new BinaryWriter(File.Open(filePath, FileMode.OpenOrCreate));
            writer.Write(region.cellWidthNum);
            writer.Write(region.cellHeightNum);
            writer.Write(region.mergeSpanNum);
            foreach (byte value in region.cellSpanCountArr)
            {
                writer.Write(value);
            }

            foreach (uint value in region.cellSpanIndexArr)
            {
                writer.Write(value);
            }

            for (int i = 0; i < region.mergeSpanNum; i++)
            {
                VoxelSpan span = region.spans[i];
                writer.Write(span.Min);
                writer.Write(span.Max);
                writer.Write(span.Offset);
            }
        }

        /// <summary>
        /// 导出前端Json体素数据
        /// </summary>
        /// <param name="dirPath"></param>
        /// <param name="clientMapVoxel"></param>
        private void ExportClientJson(string dirPath, ClientMapVoxel clientMapVoxel)
        {
            Task[] tasks = new Task[clientMapVoxel.regionNum];
            for (int i = 0; i < clientMapVoxel.regionNum; i++)
            {
                RegionVoxelData region = clientMapVoxel.regions[i];
                string filePath = Path.Combine(dirPath, $"region_{i}.json");
                tasks[i] = Task.Run(() => SaveRegionToJsonFile(region, filePath));
            }

            // 等待所有线程完成
            Task.WaitAll(tasks);

            Dictionary<string, object> dic = new Dictionary<string, object>();
            dic.Add("voxelSize", clientMapVoxel.voxelSize);
            dic.Add("voxelHeight", clientMapVoxel.voxelHeight);
            dic.Add("mapX", clientMapVoxel.mapX);
            dic.Add("mapY", clientMapVoxel.mapY);
            dic.Add("mapZ", clientMapVoxel.mapZ);
            dic.Add("buildVoxelInfo", _buildInfos);
            dic.Add("walkableSlopeAngle", _buildConfig.WalkableSlopeAngle);
            dic.Add("walkableHeight", _buildConfig.WalkableHeight);
            dic.Add("walkableClimb", _buildConfig.WalkableClimb);
            dic.Add("walkableRadius", _buildConfig.WalkableRadius);
            dic.Add("walkPointX", _walkablePoint.x * clientMapVoxel.voxelSize);
            dic.Add("walkPointY", _walkablePoint.y * clientMapVoxel.voxelHeight);
            dic.Add("walkPointZ", _walkablePoint.z * clientMapVoxel.voxelSize);
            dic.Add("regionAxisNum", clientMapVoxel.regionAxisNum);
            dic.Add("regionNum", clientMapVoxel.regionNum);
            dic.Add("regionWidth", clientMapVoxel.regionWidth);
            dic.Add("regionHeight", clientMapVoxel.regionHeight);
            File.WriteAllText(Path.Combine(dirPath, $"voxel.json"),
                JsonConvert.SerializeObject(dic, Formatting.Indented));
        }

        private void SaveRegionToJsonFile(RegionVoxelData region, string filePath)
        {
            File.WriteAllText(filePath,
                JsonConvert.SerializeObject(region, Formatting.Indented));
        }

        public void SaveServerData(string bytesFilePath, string bytesCompareFilePath)
        {
            if (_solid == null)
            {
                return;
            }

            ServerMapVoxel serverMapVoxel = new ServerMapVoxel();
            serverMapVoxel.Init(_solid);
            //整理数据，同时多线程合并数据
            int numColumns = _solid.width * _solid.height;
            //用于记录每种数量span的当前数量
            Dictionary<string, MergedServerSpan> mergedSpans = new Dictionary<string, MergedServerSpan>(numColumns);

            //这里在导出的时候，放大服务端反体素的方位，让服务端判断更为保守，来规避偏移问题,上下放大0.2米
            ushort offset = 20;
            uint currentCellIndex = 0;
            for (uint columnIndex = 0; columnIndex < numColumns; columnIndex++)
            {
                Recast.rcSpan span = _solid.spans[columnIndex];
                byte count = 0;
                //服务端需要的是反体素数据，所以这里需要反过来
                ServerSpan firstSpan = ServerSpan.Create();
                if (span == null)
                {
                    firstSpan.smin = 0;
                    firstSpan.smax = (ushort)serverMapVoxel.voxelMaxHeight;
                    firstSpan.mask = 1;
                    count++;
                }
                else
                {
                    ServerSpan preSpan = null;
                    if (span.smin != 0)
                    {
                        firstSpan.smin = 0;
                        firstSpan.smax = (ushort)Math.Min(span.smin + offset, span.smax - 1);
                        firstSpan.mask = 1;
                        preSpan = firstSpan;
                        count++;
                    }

                    ushort lastMax = (ushort)Math.Max(span.smax - offset, span.smin + 1); //记录上一个span的max值
                    span = span.next;
                    while (span != null)
                    {
                        if (preSpan == null)
                        {
                            firstSpan.smin = lastMax;
                            firstSpan.smax = (ushort)Math.Min(span.smin + offset, span.smax - 1);
                            firstSpan.mask = 1;
                            preSpan = firstSpan;
                        }
                        else
                        {
                            ServerSpan serverSpan = ServerSpan.Create();
                            serverSpan.smin = lastMax;
                            serverSpan.smax = (ushort)Math.Min(span.smin + offset, span.smax - 1);
                            serverSpan.mask = 1;
                            preSpan.next = serverSpan;
                            preSpan = serverSpan;
                        }

                        count++;
                        lastMax = (ushort)Math.Max(span.smax - offset, span.smin + 1);
                        span = span.next;
                    }

                    if (serverMapVoxel.voxelMaxHeight > lastMax)
                    {
                        if (preSpan == null)
                        {
                            firstSpan.smin = lastMax;
                            firstSpan.smax = (ushort)serverMapVoxel.voxelMaxHeight;
                            firstSpan.mask = 1;
                        }
                        else
                        {
                            ServerSpan serverSpan = ServerSpan.Create();
                            serverSpan.smin = lastMax;
                            serverSpan.smax = (ushort)serverMapVoxel.voxelMaxHeight;
                            serverSpan.mask = 1;
                            preSpan.next = serverSpan;
                        }

                        count++;
                    }
                }

                if (count > 0)
                {
                    string key = firstSpan.GetUniqueKey();
                    if (mergedSpans.TryGetValue(key, out MergedServerSpan mergedSpan))
                    {
                        // 已经存在合并的体素数据，将当前格子的列索引加入到合并后的体素数据中
                        mergedSpan.columnIndices.Add(columnIndex);
                        firstSpan.Release();
                    }
                    else
                    {
                        // 不存在合并的体素数据，创建新的合并体素数据并加入字典
                        MergedServerSpan newMergedSpan = new MergedServerSpan
                        {
                            index = currentCellIndex,
                            span = firstSpan,
                            count = count,
                            columnIndices = new List<uint> { columnIndex }
                        };
                        mergedSpans.Add(key, newMergedSpan);
                        currentCellIndex += count;
                    }
                }
                else
                {
                    firstSpan.Release();
                }
            }

            // 将键值对转换为列表，并根据值排序
            List<MergedServerSpan> mergedServerSpans = mergedSpans.Values.ToList();
            mergedServerSpans.Sort((x, y) => x.index.CompareTo(y.index));
            foreach (MergedServerSpan span in mergedServerSpans)
            {
                ServerCell cell;
                cell.count = span.count;
                cell.index = span.index;
                foreach (uint columnIndex in span.columnIndices)
                {
                    serverMapVoxel.cells[columnIndex] = cell;
                }
            }

            using (var writer = new BinaryWriter(File.Open(bytesFilePath, FileMode.OpenOrCreate)))
            {
                writer.Write(serverMapVoxel.voxelCellX);
                writer.Write(serverMapVoxel.voxelMaxHeight);
                writer.Write(serverMapVoxel.voxelCellZ);
                writer.Write(currentCellIndex); //currentCellIndex这个记录的当前合并后的span最大下标，可以用来作为span数量

                for (int x = 0; x <= serverMapVoxel.voxelCellX; x++)
                {
                    for (int z = 0; z <= serverMapVoxel.voxelCellZ; z++)
                    {
                        int dataIndex = x + z * _solid.width;
                        ServerCell cell = serverMapVoxel.cells[dataIndex];
                        writer.Write(cell.count);
                        writer.Write(cell.index);
                    }
                }

                foreach (MergedServerSpan span in mergedServerSpans)
                {
                    ServerSpan s = span.span;
                    while (s != null)
                    {
                        writer.Write(s.GetResult());
                        s = s.next;
                    }
                }
            }

            Debug.LogFormat("[VoxelViewer:SaveServerBytes] save data to {0} successfully.", bytesFilePath);


            using (StreamWriter writer = new StreamWriter(bytesCompareFilePath, false, Encoding.UTF8))
            {
                writer.WriteLine($"x:{serverMapVoxel.voxelCellX}");
                writer.WriteLine($"y:{serverMapVoxel.voxelMaxHeight}");
                writer.WriteLine($"z:{serverMapVoxel.voxelCellZ}");
                writer.WriteLine($"totalSpansNum:{currentCellIndex}");
                for (int i = 0; i < serverMapVoxel.cells.Length; i++)
                {
                    ServerCell cell = serverMapVoxel.cells[i];
                    writer.WriteLine(
                        $"x:{i % _solid.width},z:{i / _solid.width},spanNum:{cell.count},index:{cell.index}");
                }

                int index = 0;
                foreach (MergedServerSpan span in mergedServerSpans)
                {
                    ServerSpan s = span.span;
                    while (s != null)
                    {
                        writer.WriteLine($"index:{index++},result:{s.GetResult()},min:{s.smin},max:{s.smax}");
                        s = s.next;
                    }
                }
            }

            Debug.LogFormat("[VoxelViewer:SaveServerBytesCompare] save data to {0} successfully.",
                bytesCompareFilePath);
            ServerSpan.ReleaseAll();
        }

        #region 密闭空间合并

        private AntiSpan[] _antiSpans;
        private int[] _poolQueue;
        private int _poolSize;
        private AntiSpan[] _neighborPool;

        #region 密封空间合并Debug参数

        private bool _hasCheckFirst;
        public bool needDebugMergeClosedSpace { set; private get; }
        public int checkMergeAntiSpanMin { set; private get; }
        public int checkMergeAntiSpanMax { set; private get; }

        #endregion

        private void MergeClosedSpaceVoxel()
        {
            int w = _solid.width;
            int h = _solid.height;
            //可走点范围检查
            if (_walkablePoint.x > w || _walkablePoint.z > h)
            {
                Debug.LogErrorFormat(
                    "walkable point is out of range. w:{0},h:{1},_walkablePoint.x:{2},_walkablePoint.z:{3}", w, h,
                    _walkablePoint.x, _walkablePoint.z);
                return;
            }

            //可走点是否在体素中
            int walkableIndex = _walkablePoint.x + _walkablePoint.z * w;
            Recast.rcSpan span = _solid.spans[walkableIndex];
            while (span != null)
            {
                if (span.smin <= _walkablePoint.y && span.smax >= _walkablePoint.y)
                {
                    Debug.LogErrorFormat(
                        "walkable point is in a span. _walkablePoint.x:{0},_walkablePoint.z:{1},_walkablePoint.y:{2}",
                        _walkablePoint.x, _walkablePoint.z, _walkablePoint.y);
                    return;
                }

                span = span.next;
            }

            _antiSpans = new AntiSpan[w * h];
            _poolQueue = new int[w * h];
            _poolSize = 0;
            _neighborPool = new AntiSpan[w * h];
            //构建原体素的反体素
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int index = x + z * w;
                    span = _solid.spans[index];
                    //（x,y）处没有体素，则该位置以上没有实物，因此（x,y）处的反体素只有一个，上、下表面高度分别为height、0。
                    if (span == null)
                    {
                        AntiSpan antiSpan = new AntiSpan
                        {
                            Min = 0,
                            Max = Recast.RC_SPAN_MAX_HEIGHT
                        };
                        _antiSpans[index] = antiSpan;
                    }
                    else
                    {
                        //（x,y）处有体素，则该位置以上有实物，因此（x,y）处的反体素有多个,为每个span生成对应的反体素
                        AntiSpan antiSpan = new AntiSpan();
                        _antiSpans[index] = antiSpan;
                        bool hasInitFirst = false;

                        if (span.smin != 0)
                        {
                            antiSpan.Min = 0;
                            antiSpan.Max = span.smin;
                            hasInitFirst = true;
                        }

                        var lastMax = span.smax; //记录上一个span的max值
                        span = span.next;
                        while (span != null)
                        {
                            if (hasInitFirst)
                            {
                                antiSpan.Next = new AntiSpan
                                {
                                    Min = lastMax,
                                    Max = span.smin
                                };
                                antiSpan = antiSpan.Next;
                            }
                            else
                            {
                                antiSpan.Min = lastMax;
                                antiSpan.Max = span.smin;
                                hasInitFirst = true;
                            }

                            lastMax = span.smax;
                            span = span.next;
                        }

                        if (Recast.RC_SPAN_MAX_HEIGHT > lastMax)
                        {
                            if (hasInitFirst)
                            {
                                antiSpan.Next = new AntiSpan
                                {
                                    Min = lastMax,
                                    Max = Recast.RC_SPAN_MAX_HEIGHT
                                };
                            }
                            else
                            {
                                antiSpan.Min = lastMax;
                                antiSpan.Max = Recast.RC_SPAN_MAX_HEIGHT;
                            }
                        }
                    }
                }
            }

            //标记连通区。在antiSpans中采用广度优先搜索，首先在队列中放入一个可行走区上方的反体素，从该反体素开始标记整个场景的连通反体素。
            //_walkablePoint是一个可行走区上方的反体素(目前先手动定位)
            AntiSpan frontSpan = _antiSpans[_walkablePoint.x + _walkablePoint.z * w];
            while (frontSpan != null)
            {
                if (frontSpan.Min <= _walkablePoint.y && frontSpan.Max >= _walkablePoint.y)
                {
                    frontSpan.Flag = 1;
                    MarkConnectedAntiSpans(frontSpan, _walkablePoint.x, _walkablePoint.z);
                    goto BFS;
                }

                frontSpan = frontSpan.Next;
            }

            Debug.LogErrorFormat("坐标点{0},{1}找不到对应高度 {2} 的反体素，请重新设置坐标点", _walkablePoint.x, _walkablePoint.z,
                _walkablePoint.y);

            _antiSpans = null;
            _poolQueue = null;
            _poolSize = 0;
            _neighborPool = null;
            return;

            //广度优先搜索
            BFS:
            _hasCheckFirst = !needDebugMergeClosedSpace;
            int ox = 0, oz = 0;
            frontSpan = PopPool(ref ox, ref oz);
            while (frontSpan != null)
            {
                MarkConnectedAntiSpans(frontSpan, ox, oz);
                frontSpan = PopPool(ref ox, ref oz);
            }

            //对于不可联通的体素，将其设置为不可行走面，根据antiSpans 重新构建spans
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int index = x + z * w;
                    Recast.rcSpan s = _solid.spans[index];
                    if (s == null)
                    {
                        continue;
                    }

                    AntiSpan antiSpan = _antiSpans[index];
                    if (antiSpan == null)
                    {
                        continue;
                    }

                    while (s != null)
                    {
                        if (s.area == Recast.RC_WALKABLE_AREA)
                        {
                            if (antiSpan != null)
                            {
                                //不连通或者角色不能通过的反体素，都直接将其对应的体素设置为不可走
                                if (antiSpan.Flag != 1 || antiSpan.Max - antiSpan.Min < _buildConfig.WalkableHeight)
                                {
                                    s.area = Recast.RC_NULL_AREA;
                                }
                            }
                        }

                        s = s.next;
                        antiSpan = antiSpan?.Next;
                    }
                }
            }

            //再合并不可走的体素
            FillNullSpans();
            _antiSpans = null;
            _poolQueue = null;
            _poolSize = 0;
            _neighborPool = null;
            _mergeSpanSuccess = true;
        }


        private void MarkConnectedAntiSpans(AntiSpan frontSpan, int spanX, int spanZ)
        {
            int w = _solid.width;
            int h = _solid.height;
            // 检查8个相邻的体素
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    //跳过自己
                    if (dx == 0 && dz == 0)
                        continue;
                    int neighborSpanX = spanX + dx;
                    int neighborSpanZ = spanZ + dz;
                    //跳过超出边界
                    if (neighborSpanX < 0 || neighborSpanX >= w || neighborSpanZ < 0 || neighborSpanZ >= h)
                        continue;
                    int neighborIndex = neighborSpanX + neighborSpanZ * w;
                    AntiSpan span = _antiSpans[neighborIndex];
                    while (span != null)
                    {
                        if (span.Flag == 0)
                        {
                            ushort min = Math.Max(frontSpan.Min, span.Min);
                            ushort max = Math.Min(frontSpan.Max, span.Max);

                            //连通性检查，找出frontSpan和邻居反体素s的交集（min,max），如果max和min之差大于人的高度，则表示可以从frontSpan到达s，将s的flag标记为1，表示连通，并将s压入队列中
                            if (min <= max && max - min >= _buildConfig.WalkableHeight)
                            {
                                //这里Debug检查，排查联通的反体素是否有问题
                                if (!_hasCheckFirst &&
                                    (frontSpan.Min == checkMergeAntiSpanMin && frontSpan.Max == checkMergeAntiSpanMax ||
                                     span.Min == checkMergeAntiSpanMin && span.Max == checkMergeAntiSpanMax))
                                {
                                    Debug.LogErrorFormat(
                                        "spanX:{0},spanZ:{1},neighborSpanX:{2},neighborSpanZ:{3},frontSpan.Min:{4},frontSpan.Max:{5},span.Min:{6},span.Max:{7}",
                                        spanX, spanZ, neighborSpanX, neighborSpanZ, frontSpan.Min, frontSpan.Max,
                                        span.Min, span.Max);
                                    _hasCheckFirst = true;
                                }

                                span.Flag = 1;
                                AddPool(span, neighborIndex);
                            }
                        }

                        span = span.Next;
                    }
                }
            }
        }

        private void AddPool(AntiSpan antiSpan, int index)
        {
            if (_neighborPool[index] == null)
            {
                _neighborPool[index] = antiSpan;
                _poolQueue[_poolSize++] = index;
            }
            else
            {
                AntiSpan s = _neighborPool[index];
                _neighborPool[index] = antiSpan;
                antiSpan.Link = s;
            }
        }

        private AntiSpan PopPool(ref int x, ref int z)
        {
            if (_poolSize <= 0 || _poolSize >= _solid.width * _solid.height + 1)
            {
                return null;
            }

            int poolIndex = _poolQueue[_poolSize - 1];
            if (_neighborPool[poolIndex] != null)
            {
                var popSpan = _neighborPool[poolIndex];
                x = poolIndex % _solid.width;
                z = poolIndex / _solid.width;
                _neighborPool[poolIndex] = _neighborPool[poolIndex].Link;
                if (_neighborPool[poolIndex] == null)
                {
                    _poolQueue[--_poolSize] = 0;
                }

                return popSpan;
            }

            return null;
        }

        #endregion

        #region 计算体素连通性

        private UnionAntiSpan[] _unionAntiSpans;
        private Byte[] _connectFlags; //用于记录连通性的标记，0为不连通，1为连通

        /// <summary>
        /// 计算地面体素连通性
        /// </summary>
        public void CalculateConnectivity(Vector2Int point)
        {
            //一定要确保的合并过封闭空间体素，这样的话， 场景的所有反体素都是可走的，这样的话，就可以通过反体素的连通性来计算板块的连通性
            if (!_mergeSpanSuccess)
            {
                Debug.LogError("CalculateRegionConnectivity must be called after MergeClosedSpaceVoxel");
            }

            //初始化反体素
            InitUnionAntiSpan();
            //处理反体素连通性
            ConnectVoxels();
            int w = _solid.width;
            int h = _solid.height;
            _connectFlags = new byte[w * h];
            UnionAntiSpan walkableSpan = _unionAntiSpans[point.x + point.y * w];
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int index = x + z * w;
                    UnionAntiSpan antiSpan = _unionAntiSpans[index];
                    if (walkableSpan.AreConnected(antiSpan))
                    {
                        _connectFlags[index] = 1;
                    }
                }
            }
        }

        public UnionAntiSpan GetUnionAntiSpan(int x, int z)
        {
            if (_unionAntiSpans == null)
            {
                return null;
            }

            int w = _solid.width;
            int index = x + z * w;
            return _unionAntiSpans[index];
        }


        public void TestConnect(Vector2Int start, Vector2Int end)
        {
            if (_unionAntiSpans == null)
            {
                return;
            }

            int w = _solid.width;
            int startIndex = start.x + start.y * w;
            int endIndex = end.x + end.y * w;
            UnionAntiSpan startSpan = _unionAntiSpans[startIndex];
            UnionAntiSpan endSpan = _unionAntiSpans[endIndex];
            Debug.Log("isConnect-->" + (startSpan.FindRoot() == endSpan.FindRoot()));
        }

        private void InitUnionAntiSpan()
        {
            int w = _solid.width;
            int h = _solid.height;
            _unionAntiSpans = new UnionAntiSpan[w * h];
            //构建地表体素的反体素，潜规则默认取第一个span的smax作为反体素的min，如果第一个span的smin为0，则反体素的min为0
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int index = x + z * w;
                    Recast.rcSpan span = _solid.spans[index];
                    UnionAntiSpan antiSpan = new UnionAntiSpan();
                    _unionAntiSpans[index] = antiSpan;
                    if (span is not { smin: 0 })
                    {
                        antiSpan.Min = 0;
                    }
                    else
                    {
                        antiSpan.Min = span.smax;
                    }
                }
            }
        }

        /// <summary>
        /// 合并体素
        /// </summary>
        private void ConnectVoxels()
        {
            int w = _solid.width;
            int h = _solid.height;
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int index = x + z * w;
                    UnionAntiSpan unionAntiSpan = _unionAntiSpans[index];

                    // 对当前体素的上、下、左、右体素进行判断
                    UnionWithNeighbors(unionAntiSpan, x - 1, z); // 左侧体素
                    UnionWithNeighbors(unionAntiSpan, x + 1, z); // 右侧体素
                    UnionWithNeighbors(unionAntiSpan, x, z - 1); // 上方体素
                    UnionWithNeighbors(unionAntiSpan, x, z + 1); // 下方体素
                }
            }
        }

        private void UnionWithNeighbors(UnionAntiSpan currentSpan, int neighborX, int neighborZ)
        {
            if (IsInRange(neighborX, neighborZ))
            {
                int index = neighborX + neighborZ * _solid.width;
                UnionAntiSpan neighborSpans = _unionAntiSpans[index];
                // 对当前体素的每个 AntiSpan 和邻居体素的每个 AntiSpan 进行判断和合并
                if (!currentSpan.AreConnected(neighborSpans) && SpansCanConnected(currentSpan, neighborSpans))
                {
                    currentSpan.Union(neighborSpans);
                }
            }
        }

        /// <summary>
        /// 是否体素下标是否符合范围
        /// </summary>
        /// <param name="x"></param>
        /// <param name="z"></param>
        /// <returns></returns>
        private bool IsInRange(int x, int z)
        {
            return x >= 0 && x < _solid.width && z >= 0 && z < _solid.height;
        }

        /// <summary>
        /// 判断两个反体素是否可以连通
        /// </summary>
        /// <param name="spanA"></param>
        /// <param name="spanB"></param>
        /// <returns></returns>
        private bool SpansCanConnected(UnionAntiSpan spanA, UnionAntiSpan spanB)
        {
            //两个span的高度差小于人物高度，就认为可连通
            return Mathf.Abs(spanA.Min - spanB.Min) <= _buildConfig.WalkableHeight;
        }

        #endregion
    }
}