using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
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

    public class VoxelTool
    {
        private MeshData _meshData;
        private VoxelBuildConfig _buildConfig;

        private readonly BuildContext _ctx;

        private byte[] _triAreas;
        private Recast.rcHeightfield _solid;
        private Recast.rcCompactHeightfield _chf;
        private Recast.rcPolyMesh _polyMesh;
        private Recast.rcPolyMeshDetail _polyMeshDetail;
        private byte[] _navMeshData;
        private int _navMeshDataSize = 0;
        bool _buildVoxelSuccess;

        bool _filterLowHangingObstacles = true;
        bool _filterLedgeSpans = true;
        bool _filterWalkableLowHeightSpans = true;

        public const int SpanElementNum = 3; // min，max，area

        public bool buildVoxelSuccess => _buildVoxelSuccess;

        public VoxelTool()
        {
            _meshData = new MeshData();
            _buildConfig = new VoxelBuildConfig();
            _buildConfig.bmin = new float[3];
            _buildConfig.bmax = new float[3];
            _ctx = new BuildContext();
        }

        public void Reset()
        {
            _meshData.Release();
            Cleanup();
        }


        public void SetBuildConfig(float cellSize, float cellHeight, float agentHeight, float agentClimbHeight,
            float agentRadius, float agentMaxSlope)
        {
            _buildConfig.cellSize = cellSize;
            _buildConfig.cellHeight = cellHeight;
            _buildConfig.walkableSlopeAngle = agentMaxSlope;
            _buildConfig.walkableHeight = Mathf.CeilToInt(agentHeight / cellHeight);
            _buildConfig.waterThresold = (int)(_buildConfig.walkableHeight * 0.8f);
            _buildConfig.walkableClimb = Mathf.FloorToInt(agentClimbHeight / cellHeight);
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

        public float width => _buildConfig.width;
        public float height => _buildConfig.height;
        public float cellSize => _buildConfig.cellSize;


        /// <summary>
        /// 添加网格数据
        /// </summary>
        /// <param name="vertices"></param>
        /// <param name="vertexNum"></param>
        /// <param name="triangles"></param>
        /// <param name="triangleNum"></param>
        /// <param name="area"></param>
        /// <returns></returns>
        public void AddMesh(float[] vertices, int vertexNum, int[] triangles, int triangleNum, byte area)
        {
            _meshData.AddMesh(vertices, vertexNum, triangles, triangleNum, area);
        }

        /// <summary>
        /// 构造体素
        /// </summary>
        /// <returns></returns>
        public bool BuildVoxel()
        {
            //计算格子数量
            Recast.rcCalcGridSize(_buildConfig.bmin, _buildConfig.bmax, _buildConfig.cellSize, out _buildConfig.width,
                out _buildConfig.height);

            // Reset build times gathering.
            _ctx.resetTimers();

            _ctx.log(Recast.rcLogCategory.RC_LOG_PROGRESS,
                $"Building voxel: {_buildConfig.width} x {_buildConfig.height} cells ,{_buildConfig.bmin[0]},{_buildConfig.bmin[1]},{_buildConfig.bmin[2]},{_buildConfig.bmax[0]},{_buildConfig.bmax[1]},{_buildConfig.bmax[2]}");

            // Start the build process.	
            _ctx.startTimer(Recast.rcTimerLabel.RC_TIMER_TOTAL);

            //
            // Step 2. Rasterize input polygon soup.
            //

            // Allocate voxel heightfield where we rasterize our input data to.
            _solid = new Recast.rcHeightfield();
            if (!Recast.rcCreateHeightfield(_ctx, _solid, _buildConfig.width, _buildConfig.height, _buildConfig.bmin,
                    _buildConfig.bmax, _buildConfig.cellSize, _buildConfig.cellHeight))
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
            Recast.rcMarkWalkableTriangles(_ctx, _buildConfig.walkableSlopeAngle, _meshData.vertices,
                _meshData.vertexNum,
                _meshData.triangles, _meshData.triangleNum, _triAreas);
            // 光栅化三角面，用walkableclimb进行上下合并
            if (!Recast.rcRasterizeTriangles(_ctx, _meshData.vertices, _meshData.vertexNum, _meshData.triangles,
                    _triAreas,
                    _meshData.triangleNum, _solid, _buildConfig.walkableClimb))
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

            //
            // Step 4. Partition walkable surface to simple regions.
            //
            _chf = new Recast.rcCompactHeightfield();
            // 构建CompactHeightfield 紧缩高度场，包含neighbours信息，加速后期处理
            if (!Recast.rcBuildCompactHeightfield(_ctx, _buildConfig.walkableHeight, _buildConfig.walkableClimb, _solid,
                    _chf))
            {
                _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR, "buildNavigation: Could not build compact data.");
                return false;
            }

            // Erode the walkable area by agent radius.
            // 构建agentRadius区域，把距离边缘walkableRadius内的span设置为不可行走
            if (!Recast.rcErodeWalkableArea(_ctx, _buildConfig.walkableRadius, _chf))
            {
                _ctx.log(Recast.rcLogCategory.RC_LOG_ERROR, "buildNavigation: Could not erode.");
                return false;
            }


            _ctx.stopTimer(Recast.rcTimerLabel.RC_TIMER_TOTAL);
            _ctx.log(Recast.rcLogCategory.RC_LOG_PROGRESS,
                $"build time: {_ctx.getAccumulatedTime(Recast.rcTimerLabel.RC_TIMER_TOTAL) * 0.001f}");

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
                Recast.rcFilterLowHangingWalkableObstacles(_ctx, _buildConfig.walkableClimb, _solid);
            // 过滤突起span，如果从span顶端到相邻区间下降距离超过了WalkableClimb，那么这个span被认为是个突起，不可行走。这里的相邻区域定义为前后左右四个方向。
            // span比某个邻居span的上表面高出walkClimb，说明从span到邻居span有一个落差，则把span标记为不可行走。
            // span与其他邻居上表面相比都在walkClimb之内，但是邻居span之间的上下表面高度差超过walkClimb，说明span处于比较陡峭的地方，则把span标记为不可行走。
            if (_filterLedgeSpans)
                Recast.rcFilterLedgeSpans(_ctx, _buildConfig.walkableHeight, _buildConfig.walkableClimb, _solid);
            // 过滤可行走的低高度span。当span上方有距离小于walkableHeight的障碍物时，它的顶端表面也不可行走，因为容纳的高度不够
            //如果上下两个span之间的空隙小于等于walkHeight，则把下span标记为不可行走。
            if (_filterWalkableLowHeightSpans)
                Recast.rcFilterWalkableLowHeightSpans(_ctx, _buildConfig.walkableHeight, _solid);
        }

        struct AddSpanInfo
        {
            public int x;
            public int y;
            public ushort smin;
            public ushort smax;
            public byte area;
        }

        private void FillNullSpans()
        {
            List<AddSpanInfo> addSpanList = new List<AddSpanInfo>();

            int w = _solid.width;
            int h = _solid.height;

            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
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
                                smax = s.next?.smin ?? Recast.RC_SPAN_MAX_HEIGHT,
                                area = s.area,
                            };
                            addSpanList.Add(info);
                        }
                    }
                }
            }

            foreach (AddSpanInfo info in addSpanList)
            {
                Recast.rcAddSpan(_ctx, _solid, info.x, info.y, info.smin, info.smax, info.area,
                    _buildConfig.walkableClimb);
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


        public int GetSpans(int x, int z, ushort[] buffer, uint maxNum)
        {
            if (!_buildVoxelSuccess)
                return -1;
            if (x < 0 || x >= _solid.width || z < 0 || z >= _solid.height)
            {
                return -1;
            }

            uint counter = 0;
            Recast.rcSpan span = _solid.spans[x + z * _solid.width];
            while (span != null && counter < maxNum)
            {
                {
                    buffer[counter * SpanElementNum] = span.smin;
                    buffer[counter * SpanElementNum + 1] = span.smax;
                    buffer[counter * SpanElementNum + 2] = span.area;
                    ++counter;
                }

                span = span.next;
            }

            return (int)counter;
        }

        public int GetSpanNum(int x, int z)
        {
            if (!_buildVoxelSuccess)
                return -1;
            if (x < 0 || x >= _solid.width || z < 0 || z >= _solid.height)
            {
                return -1;
            }

            uint counter = 0;
            Recast.rcSpan span = _solid.spans[x + z * _solid.width];
            while (span != null)
            {
                if (span.area != Recast.RC_NULL_AREA)
                {
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
            _buildVoxelSuccess = false;
        }

        public bool SaveFullJsonData(string dirPath, int regionSize)
        {
            if (_solid == null)
            {
                return false;
            }

            JsonMapVoxel mapVoxel = new JsonMapVoxel();
            mapVoxel.Init(_solid, regionSize);
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

                        Recast.rcSpan s = _solid.spans[cellX + cellY * _solid.width];
                        Recast.rcSpan begin = s;
                        while (s != null)
                        {
                            ++spNum;
                            s = s.next;
                        }

                        CellSpanInfo info = new CellSpanInfo(cellX, cellY, spNum);
                        for (int j = 0; j < spNum; j++)
                        {
                            info.spans[j] = new VoxelSpan(begin.smin, begin.smax, begin.area);
                            begin = begin.next;
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
            for (int i = 0; i < clientMapVoxel.regionNum; i++)
            {
                int cx = i % clientMapVoxel.regionWidth * regionSize;
                int cy = i / clientMapVoxel.regionHeight * regionSize;
                RegionVoxelData region = new RegionVoxelData();
                region.index = i;
                clientMapVoxel.regions[i] = region;
                region.cellWidthNum = (ushort)(cx + regionSize <= _solid.width ? regionSize : _solid.width - cx);
                region.cellHeightNum = (ushort)(cy + regionSize <= _solid.height ? regionSize : _solid.height - cy);

                int cellCount = region.cellHeightNum * region.cellWidthNum;
                VoxelSpan[][] regionCellSpans = new VoxelSpan[cellCount][];
                cellSpans[i] = regionCellSpans;

                int totalSpanNum = 0;
                uint tmpCellIdx = 0;
                for (int y = 0; y < region.cellHeightNum; ++y)
                {
                    for (int x = 0; x < region.cellWidthNum; ++x)
                    {
                        byte spNum = 0;
                        int realIndex = x + cx + (y + cy) * _solid.width;
                        for (Recast.rcSpan s = _solid.spans[realIndex]; s != null; s = s.next)
                        {
                            spNum++;
                            totalSpanNum++;
                        }

                        if (spNum > 0)
                        {
                            VoxelSpan[] spans = new VoxelSpan[spNum];
                            int tmpSpanIdx = 0;
                            for (Recast.rcSpan s = _solid.spans[realIndex]; s != null; s = s.next)
                            {
                                spans[tmpSpanIdx] = new VoxelSpan(s.smin, s.smax, s.area);
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
            Dictionary<string, MergedServerSpan> mergedSpans =
                new Dictionary<string, MergedServerSpan>(GetTotalSpanCount());
            uint currentCellIndex = 0;
            for (uint columnIndex = 0; columnIndex < numColumns; columnIndex++)
            {
                Recast.rcSpan span = _solid.spans[columnIndex];
                byte count = 0;

                ServerSpan firstSpan = ServerSpan.Create();
                ServerSpan preSpan = null;
                while (span != null)
                {
                    // 构建空心体素
                    ServerSpan serverSpan;
                    if (preSpan == null)
                    {
                        serverSpan = firstSpan;
                    }
                    else
                    {
                        serverSpan = ServerSpan.Create();
                    }

                    int bot = span.smax;
                    int top = span.next?.smin ?? serverMapVoxel.voxelMaxHeight;
                    serverSpan.smin = (ushort)Recast.rcClamp(bot, 0, serverMapVoxel.voxelMaxHeight);
                    serverSpan.smax = (ushort)Recast.rcClamp(top, 0, serverMapVoxel.voxelMaxHeight);
                    serverSpan.mask = 1;
                    count++;
                    if (preSpan != null)
                    {
                        preSpan.next = serverSpan;
                    }
                    else
                    {
                        preSpan = serverSpan;
                    }

                    span = span.next;
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
                writer.Write(serverMapVoxel.voxelCellY);
                writer.Write(currentCellIndex); //currentCellIndex这个记录的当前合并后的span最大下标，可以用来作为spna数量
                foreach (ServerCell cell in serverMapVoxel.cells)
                {
                    writer.Write(cell.count);
                    writer.Write(cell.index);
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
                writer.WriteLine($"z:{serverMapVoxel.voxelCellY}");
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
                        writer.WriteLine($"index:{index++},result:{s.GetResult()},min:{s.smin},max:{s.smin}");
                        s = s.next;
                    }
                }
            }

            Debug.LogFormat("[VoxelViewer:SaveServerBytesCompare] save data to {0} successfully.",
                bytesCompareFilePath);
            ServerSpan.ReleaseAll();
        }

        /// <summary>
        /// 获取当前生成的所有VoxelSpan数量(不管area标记)
        /// </summary>
        /// <returns></returns>
        public int GetTotalSpanCount()
        {
            int spanCount = 0;
            if (_solid != null)
            {
                int w = _solid.width;
                int h = _solid.height;
                for (int y = 0; y < h; ++y)
                {
                    for (int x = 0; x < w; ++x)
                    {
                        for (Recast.rcSpan s = _solid.spans![x + y * w]; s != null; s = s.next)
                        {
                            spanCount++;
                        }
                    }
                }
            }

            return spanCount;
        }

        #region 密闭空间合并

        private ReverseSpan[] _reverseSpans;

        private void GenReverseSpan()
        {
            int w = _solid.width;
            int h = _solid.height;
            _reverseSpans = new ReverseSpan[w * h];
            ReverseSpan.ReverseSpanCount = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    ReverseSpan curRevSpan = new ReverseSpan();
                    int index = x + y * w;
                    Recast.rcSpan s = _solid.spans[index];
                    _reverseSpans[index] = curRevSpan;
                    if (s == null)
                    {
                        curRevSpan.smin = 0;
                        curRevSpan.smax = Recast.RC_SPAN_MAX_HEIGHT;
                        ReverseSpan.ReverseSpanCount++;
                    }
                    else
                    {
                        Recast.rcSpan lastSpan = s;
                        if (s.smin != 0)
                        {
                            curRevSpan.smin = 0;
                            curRevSpan.smax = s.smin;
                            curRevSpan.next = new ReverseSpan();
                            curRevSpan = curRevSpan.next;
                            ReverseSpan.ReverseSpanCount++;
                        }

                        while (s.next != null)
                        {
                            curRevSpan.smin = s.smax;
                            curRevSpan.smax = s.next.smin;
                            s = lastSpan = s.next;
                            curRevSpan.next = new ReverseSpan();
                            curRevSpan = curRevSpan.next;
                            ReverseSpan.ReverseSpanCount++;
                        }

                        if (lastSpan.smax != Recast.RC_SPAN_MAX_HEIGHT)
                        {
                            curRevSpan.smin = lastSpan.smax;
                            curRevSpan.smax = Recast.RC_SPAN_MAX_HEIGHT;
                        }
                    }
                }
            }
        }

        private void DoNeighbor()
        {
            
        }

        #endregion
    }
}