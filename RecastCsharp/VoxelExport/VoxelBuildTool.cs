﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Pool;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using Random = UnityEngine.Random;

namespace RecastSharp
{
    [Serializable]
    public class CollectMeshInfo
    {
        [LabelText("网格根节点")] public GameObject target;
        [LabelText("网格是否有LOD")] public Boolean hasLod;
    }

    public class VoxelBuildTool : MonoBehaviour
    {
        private static readonly uint SpanBufferSize = 32;
        private static readonly ushort[] SpanBuffer = new ushort[SpanBufferSize * VoxelTool.SpanElementNum];
        private const string VoxelTopMesh = "VoxelTopMesh";
        private const string NavMeshBakeLayerName = "NavMeshBake";
        private const int MaxVerticesPerSubMesh = 65535;


        //GameEditor中间数据目录
        private const string DefaultGameEditorResPath =
            "../../packages/game/com.nemo.game.editor/Res/SceneEditor/Map";

        //服务器数据目录
        private const string DefaultServerDataPath = "../../../scene_mask";

        //客户端数据目录
        private const string DefaultClientDataPath = "../../public/config_bin/map";

        private void OnValidate()
        {
            if (string.IsNullOrEmpty(cvClientDataPath))
            {
                cvClientDataPath = Path.Combine(Application.dataPath, DefaultClientDataPath);
            }

            if (string.IsNullOrEmpty(csServerDataPath))
            {
                csServerDataPath = Path.Combine(Application.dataPath, DefaultServerDataPath);
            }

            if (string.IsNullOrEmpty(cvDebugDataPath))
            {
                cvDebugDataPath = Path.Combine(Application.dataPath, DefaultGameEditorResPath);
            }
        }

        [FoldoutGroup("通用参数")] [LabelText("地图ID")]
        public int mapID = 10000;

        [FoldoutGroup("通用参数")] [LabelText("地图可行走区域width(X轴)")] [Range(0, 5000)]
        public int xSize = 500;

        [FoldoutGroup("通用参数")] [LabelText("地图可行走区域最大垂直高度(Y轴)")] [Range(0, 650)]
        public int ySize = 500;

        [FoldoutGroup("通用参数")] [LabelText("地图可行走区域height(Z轴)")] [Range(0, 5000)]
        public int zSize = 500;

        [FoldoutGroup("通用参数")] [LabelText("体素处理网格对象")]
        public List<CollectMeshInfo> collectMeshInfos;


        #region Collider Voxel(碰撞体素)

        private VoxelTool _colliderVoxelTool;

        [FoldoutGroup("Collider Voxel")] [LabelText("碰撞体素大小(长宽)")] [Range(0.01f, 5.0f)]
        public float cvVoxelSize = 0.5f;

        [FormerlySerializedAs("cvCellHeight")]
        [FoldoutGroup("Collider Voxel")]
        [LabelText("碰撞体素高度")]
        [Range(0.01f, 5.0f)]
        public float cvVoxelHeight = 0.01f;


        [FoldoutGroup("Collider Voxel")] [LabelText("碰撞体素格子范围"), ReadOnly]
        public Vector3Int cvMaxCell = Vector3Int.zero;

        [FoldoutGroup("Collider Voxel")] [LabelText("Agent Height")] [Range(0.0f, 10.0f)]
        public float cvAgentHeight = 1.0f;

        [FoldoutGroup("Collider Voxel")] [LabelText("Agent Radius")] [Range(0.0f, 10.0f)]
        public float cvAgentRadius = 0.5f;

        [FoldoutGroup("Collider Voxel")] [LabelText("Climb Height")] [Range(0.0f, 999.0f)]
        public float cvClimbHeight = 0.5f;

        [FoldoutGroup("Collider Voxel")] [LabelText("Max Slope")] [Range(0.0f, 60.0f)]
        public float cvMaxSlope = 60.0f;

        [FoldoutGroup("Collider Voxel/过滤")]
        [LabelText("Filter Low Hanging Obstacles"), Tooltip("体素上表面之差小于walkClimb,上面的体素变为可行走,过滤悬空的可走障碍物")]
        public bool cvFilterLowHangingObstacles = true;

        [FoldoutGroup("Collider Voxel/过滤")] [LabelText("Filter Ledge Spans"), Tooltip("体素与邻居体素上表面之差超过这个高度，则变为不可走")]
        public bool cvFilterLedgeSpans;

        [FoldoutGroup("Collider Voxel/过滤")]
        [LabelText("Filter Walkable Low Height Spans"), Tooltip("上下体素之间空隙高度小于walkHeight，下体素变为不可行走")]
        public bool cvFilterWalkableLowHeightSpans = true;

        [FoldoutGroup("Collider Voxel/优化")] [LabelText("地图中必定可到达的坐标点(世界坐标)")]
        public Vector3Int cvWalkablePoint;

        [FoldoutGroup("Collider Voxel")] [LabelText("体素数据分块范围(单位米)")]
        public ushort cvRegionSize = 64;


        [FoldoutGroup("Collider Voxel")]
        [LabelText("碰撞体素客户端数据存储路径")]
        [FolderPath(ParentFolder = "Assets", AbsolutePath = true)]
        [InlineButton("OpenClientFolder", "打开目录")]
        public string cvClientDataPath;

        [FoldoutGroup("Collider Voxel")]
        [LabelText("碰撞体素服务器数据存储路径")]
        [FolderPath(ParentFolder = "Assets", AbsolutePath = true)]
        [InlineButton("OpenServerFolder", "打开目录")]
        public string csServerDataPath;

        [FoldoutGroup("Collider Voxel")]
        [LabelText("体素Json数据导出路径(debug查数据)")]
        [FolderPath(ParentFolder = "Assets", AbsolutePath = true)]
        [InlineButton("OpenDebugFolder", "打开目录")]
        public string cvDebugDataPath;

        public void OpenClientFolder()
        {
            if (cvClientDataPath != null && Directory.Exists(cvClientDataPath))
            {
                System.Diagnostics.Process.Start(cvClientDataPath);
            }
            else
            {
                Debug.LogErrorFormat("非法路径，打开失败，当前需打开路径：{0}", cvClientDataPath);
            }
        }

        public void OpenServerFolder()
        {
            if (csServerDataPath != null && Directory.Exists(csServerDataPath))
            {
                System.Diagnostics.Process.Start(csServerDataPath);
            }
            else
            {
                Debug.LogErrorFormat("非法路径，打开失败，当前需打开路径：{0}", csServerDataPath);
            }
        }

        public void OpenDebugFolder()
        {
            if (cvDebugDataPath != null && Directory.Exists(cvDebugDataPath))
            {
                System.Diagnostics.Process.Start(cvDebugDataPath);
            }
            else
            {
                Debug.LogErrorFormat("非法路径，打开失败，当前需打开路径：{0}", cvDebugDataPath);
            }
        }

        [FoldoutGroup("Collider Voxel/导出")]
        [Button("构建碰撞体素")]
        private void BuildColliderVoxel()
        {
            // 设置体素生成参数
            if (_colliderVoxelTool == null)
            {
                _colliderVoxelTool = new VoxelTool();
            }

            //
            // Step 1. Initialize build config.
            //
            _colliderVoxelTool.Reset();
            _colliderVoxelTool.SetBuildConfig(cvVoxelSize, cvVoxelHeight, cvAgentHeight, cvClimbHeight, cvAgentRadius,
                cvMaxSlope);
            _colliderVoxelTool.SetBoundBox(0, 0, 0, +xSize, ySize, zSize);
            _colliderVoxelTool.filterLowHangingObstacles = cvFilterLowHangingObstacles;
            _colliderVoxelTool.filterLedgeSpans = cvFilterLedgeSpans;
            _colliderVoxelTool.filterWalkableLowHeightSpans = cvFilterWalkableLowHeightSpans;
            //这里的walkablePoint是世界坐标，需要转换成体素坐标
            _colliderVoxelTool.point = new Vector3Int(Mathf.FloorToInt(cvWalkablePoint.x / cvVoxelSize),
                Mathf.FloorToInt(cvWalkablePoint.y / cvVoxelHeight),
                Mathf.FloorToInt(cvWalkablePoint.z / cvVoxelSize));

            cvMaxCell.Set(Mathf.CeilToInt(xSize / cvVoxelSize) - 1,
                Mathf.CeilToInt(ySize / cvVoxelHeight) - 1,
                Mathf.CeilToInt(zSize / cvVoxelSize) - 1);


            // 收集Mesh数据
            bool addMeshSuccess = _colliderVoxelTool.CollectVoxelMesh(collectMeshInfos);
            if (!addMeshSuccess)
            {
                return;
            }

            if (_colliderVoxelTool.BuildVoxel())
            {
                int cellx = -1, cellz = -1;
                uint maxSpanNum = 0;
                uint totalSpanNum = 0;
                for (int z = 0; z <= cvMaxCell.z; ++z)
                {
                    for (int x = 0; x <= cvMaxCell.x; ++x)
                    {
                        uint spanNum = _colliderVoxelTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
                        if (spanNum > maxSpanNum)
                        {
                            maxSpanNum = spanNum;
                            cellx = x;
                            cellz = z;
                        }

                        totalSpanNum += spanNum;
                    }
                }

                Debug.LogFormat("Build Voxel Success. MaxSpanNumInCell: {0} x: {1} z: {2},totalSpan:{3}", maxSpanNum,
                    cellx,
                    cellz, totalSpanNum);
            }
            else
            {
                Debug.LogError("Build Voxel Fail");
            }

            GC.Collect();
        }

        private GameObject _graphRoot;
        private GameObject _clusters;
        private GameObject _nodes;
        private GameObject _edges;
        private ObjectPool<LineRenderer> _pool;
        private List<LineRenderer> _gettedLineRenderers = new();
        private LineRenderer _lineRenderer;

        private LineRenderer lineRenderer
        {
            get
            {
                if (_lineRenderer == null)
                {
                    GameObject obj =
                        AssetDatabase.LoadAssetAtPath<GameObject>(
                            "Packages/com.nemo.game.editor/Res/Voxel/Line.prefab");
                    _lineRenderer = obj.GetComponent<LineRenderer>();
                }

                return _lineRenderer;
            }
        }

        private void DefineLineRendererPool()
        {
            if (_pool != null)
            {
                return;
            }

            _pool = new ObjectPool<LineRenderer>(() => Instantiate(lineRenderer),
                lr => { lr.gameObject.SetActive(true); }, lr =>
                {
                    lr.gameObject.SetActive(false);
                    lr.enabled = false;
                },
                lr => { Destroy(lr.gameObject); }, true, 10);
        }

        private void ResetAllLineRenderers()
        {
            if (_gettedLineRenderers.Count <= 0) return;

            foreach (var element in _gettedLineRenderers)
            {
                element.enabled = false;
                _pool.Release(element);
            }

            _gettedLineRenderers.Clear();
        }

        public LineRenderer GetLineRenderer()
        {
            var item = _pool.Get();
            _gettedLineRenderers.Add(item);
            return item;
        }

        public void ReleaseLineRenderer(LineRenderer lr)
        {
            if (!_gettedLineRenderers.Contains(lr)) return;

            _pool.Release(lr);
            _gettedLineRenderers.Remove(lr);
        }

        [FoldoutGroup("Collider Voxel/导出")] [LabelText("地表必联通的体素下标")]
        public Vector2Int connectVoxelIndex =
            Vector2Int.zero;

        [FoldoutGroup("Collider Voxel/导出")]
        [Button("构造地表体素联通")]
        public void BuildConnect()
        {
            GC.Collect();
            if (_colliderVoxelTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelViewer:SaveJsonData] build voxel success first.");
                return;
            }

            int regionCellSize = Mathf.CeilToInt(cvRegionSize / cvVoxelSize);
            _colliderVoxelTool.CalculateConnectivity(connectVoxelIndex, regionCellSize);
            // 下面的代码是显示联通区域
            // if (showCube > 0)
            // {
            //     UnionAntiSpan span = _colliderVoxelTool.GetUnionAntiSpan(start.x, start.y);
            //     if (span == null)
            //     {
            //         Debug.LogError($"[VoxelViewer:ShowConnectSpan] span is null");
            //         return;
            //     }
            //
            //     ClearTempSpan();
            //
            //     for (int z = 0; z <= cvMaxCell.z; ++z)
            //     {
            //         for (int x = 0; x <= cvMaxCell.x; ++x)
            //         {
            //             UnionAntiSpan s = _colliderVoxelTool.GetUnionAntiSpan(x, z);
            //             if (s == null) continue;
            //             if (showCube == 1 && !span.AreConnected(s))
            //             {
            //                 continue;
            //             }
            //
            //             if (showCube == 2 && span.AreConnected(s))
            //             {
            //                 continue;
            //             }
            //
            //             uint spanNum = _colliderVoxelTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
            //             if (spanNum == 0) continue;
            //             ushort min = SpanBuffer[0],
            //                 max = SpanBuffer[1],
            //                 area = SpanBuffer[2];
            //             GameObject cube = SetCube(x, z, 0, min, max, area, cvVoxelSize, connectMat);
            //             _tempVoxelCubeList.Add(cube);
            //         }
            //     }
            // }


            DrawClusters();

            GC.Collect();
        }

        private void DeleteClusters()
        {
            if (_clusters != null)
            {
                DestroyImmediate(_clusters);
            }

            if (_nodes != null)
            {
                DestroyImmediate(_nodes);
            }

            if (_edges != null)
            {
                DestroyImmediate(_edges);
            }
        }

        private void DrawClusters()
        {
            if (_graphRoot == null)
            {
                _graphRoot = new GameObject("GraphRoot");
            }

            List<Cluster> clusters = _colliderVoxelTool.graph?.levelClusters?.First();
            if (clusters == null)
            {
                Debug.LogError("[VoxelViewer:DrawClusters] clusters is null.");
                return;
            }

            DeleteClusters();
            _clusters = new GameObject("Clusters");
            _clusters.transform.SetParent(_graphRoot.transform, false);
            _nodes = new GameObject("Nodes");
            _nodes.transform.SetParent(_graphRoot.transform, false);
            _edges = new GameObject("Edges");
            _edges.transform.SetParent(_graphRoot.transform, false);
            DrawVoxelGrid();
            HashSet<GridTile> visited = new HashSet<GridTile>();
            DefineLineRendererPool();
            ResetAllLineRenderers();
            foreach (Cluster c in clusters)
            {
                DrawCluster(c);

                foreach (KeyValuePair<GridTile, Node> node in c.Nodes)
                {
                    visited.Add(node.Key);

                    //Draw Edges
                    foreach (Edge e in node.Value.edges)
                    {
                        if (!visited.Contains(e.End.pos))
                        {
                            LineRenderer line = GetLineRenderer();
                            //Draw the edge
                            line.SetPosition(0, new Vector3((e.Start.pos.x + 0.5f) * cvVoxelSize, 1,
                                (e.Start.pos.y + 0.5f) * cvVoxelSize));
                            line.SetPosition(1, new Vector3(
                                (e.End.pos.x + 0.5f) * cvVoxelSize, 1,
                                (e.End.pos.y + 0.5f) * cvVoxelSize));
                            line.gameObject.transform.SetParent(_edges.transform);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 渲染体素网格
        /// </summary>
        private void DrawVoxelGrid()
        {
            int vertexCount = (cvMaxCell.x + 1) * (cvMaxCell.z + 1);
            Vector3[] vertices = new Vector3[vertexCount];

            int vertIndex = 0;
            for (int x = 0; x <= cvMaxCell.x; x++)
            {
                for (int y = 0; y <= cvMaxCell.z; y++)
                {
                    vertices[vertIndex] = new Vector3(x * cvVoxelSize, 0, y * cvVoxelSize);
                    vertIndex++;
                }
            }

            int[] triangles = new int[cvMaxCell.x * cvMaxCell.z * 6]; // Assuming each cell is made of 2 triangles

            int index = 0;
            for (int x = 0; x < cvMaxCell.x; x++)
            {
                for (int y = 0; y < cvMaxCell.z; y++)
                {
                    int baseIndex = x * (cvMaxCell.z + 1) + y;
                    triangles[index++] = baseIndex;
                    triangles[index++] = baseIndex + 1;
                    triangles[index++] = baseIndex + cvMaxCell.z + 1;

                    triangles[index++] = baseIndex + 1;
                    triangles[index++] = baseIndex + cvMaxCell.z + 2;
                    triangles[index++] = baseIndex + cvMaxCell.z + 1;
                }
            }

            Mesh mesh = GenerateMesh("VoxelGrid", vertices, triangles);
            GameObject meshObj = new GameObject("VoxelGrid");
            meshObj.transform.parent = _nodes.transform;
            MeshFilter filter = meshObj.AddComponent<MeshFilter>();
            filter.mesh = mesh;
            MeshRenderer meshRenderer = meshObj.AddComponent<MeshRenderer>();
            Material[] materials = new Material[mesh.subMeshCount];
            Array.Fill(materials, graphMat);
            meshRenderer.materials = materials;
        }

        private void DrawCluster(Cluster c)
        {
            int vertexCount = c.Nodes.Count;
            Vector3[] vertices = new Vector3[vertexCount * 4];
            Color[] colors = new Color[vertexCount * 4];
            int[] triangles = new int[vertexCount * 6];
            int vertexNum = 0;
            int triangleNum = 0;
            Color randomColor = new Color(Random.value, Random.value, Random.value);
            //2. Draw edges
            foreach (KeyValuePair<GridTile, Node> node in c.Nodes)
            {
                GridTile gridTile = node.Key;
                // point0
                Vector3 v0 = new Vector3((gridTile.x + 0) * cvVoxelSize, 0, (gridTile.y + 0) * cvVoxelSize);
                vertices[vertexNum] = v0;
                colors[vertexNum] = randomColor;
                // point1
                Vector3 v1 = new Vector3((gridTile.x + 1) * cvVoxelSize, 0, (gridTile.y + 1) * cvVoxelSize);
                vertices[vertexNum + 1] = v1;
                colors[vertexNum + 1] = randomColor;
                // point2
                Vector3 v2 = new Vector3((gridTile.x + 1) * cvVoxelSize, 0, (gridTile.y + 0) * cvVoxelSize);
                vertices[vertexNum + 2] = v2;
                colors[vertexNum + 2] = randomColor;
                // point3
                Vector3 v3 = new Vector3((gridTile.x + 0) * cvVoxelSize, 0, (gridTile.y + 1) * cvVoxelSize);
                vertices[vertexNum + 3] = v3;
                colors[vertexNum + 3] = randomColor;

                triangles[triangleNum] = vertexNum + 0;
                triangles[triangleNum + 1] = vertexNum + 1;
                triangles[triangleNum + 2] = vertexNum + 2;
                triangles[triangleNum + 3] = vertexNum + 1;
                triangles[triangleNum + 4] = vertexNum + 0;
                triangles[triangleNum + 5] = vertexNum + 3;

                vertexNum += 4;
                triangleNum += 6;
            }

            Mesh mesh = GenerateMesh("Cluster", vertices, triangles);
            GameObject meshObj = new GameObject("Cluster");
            meshObj.transform.parent = _clusters.transform;
            MeshFilter filter = meshObj.AddComponent<MeshFilter>();
            filter.mesh = mesh;
            MeshRenderer meshRenderer = meshObj.AddComponent<MeshRenderer>();
            Material[] materials = new Material[mesh.subMeshCount];
            Array.Fill(materials, nodeMat);
            meshRenderer.materials = materials;
        }


        [FoldoutGroup("Collider Voxel/导出")]
        [Button("测试地表Span联通")]
        public void TestConnect(Vector2Int start, Vector2Int end)
        {
            GC.Collect();
            if (_colliderVoxelTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelViewer:SaveJsonData] build voxel success first.");
                return;
            }

            _colliderVoxelTool.TestConnect(start, end);

            GC.Collect();
        }

        [FoldoutGroup("Collider Voxel/导出")]
        [Button("Offset测试")]
        public void TestOffset(Vector2Int pos)
        {
            GC.Collect();
            if (_colliderVoxelTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelViewer:SaveJsonData] build voxel success first.");
                return;
            }

            _colliderVoxelTool.TestOffset(pos.x, pos.y);

            GC.Collect();
        }

        [FoldoutGroup("Collider Voxel/导出")]
        [Button("导出体素完整Json数据(二次处理/查数据)")]
        public void ExportFullJsonData()
        {
            GC.Collect();
            if (_colliderVoxelTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelViewer:SaveJsonData] build voxel success first.");
                return;
            }

            string dirPath = Path.Combine(cvDebugDataPath, $"{mapID}", "voxel_total_json");
            if (Directory.Exists(dirPath))
            {
                Directory.Delete(dirPath, true);
            }

            Directory.CreateDirectory(dirPath);
            int regionCellSize = Mathf.CeilToInt(cvRegionSize / cvVoxelSize);
            if (_colliderVoxelTool.SaveFullJsonData(dirPath, regionCellSize))
            {
                Debug.LogFormat("[VoxelViewer:SaveFullJsonData] save data to {0} successfully.", dirPath);
            }
            else
            {
                Debug.LogErrorFormat("[VoxelViewer:SaveFullJsonData] save data to {0} failed.", dirPath);
            }

            GC.Collect();
        }

        private void CreateDir(string dirPath)
        {
            if (Directory.Exists(dirPath))
            {
                Directory.Delete(dirPath, true);
            }

            Directory.CreateDirectory(dirPath);
        }

        [FoldoutGroup("Collider Voxel/导出")]
        [Button("导出客户端碰撞体素数据"), ResponsiveButtonGroup("Collider Voxel/导出/Client")]
        public void ExportClientData()
        {
            GC.Collect();
            if (_colliderVoxelTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelViewer:SaveClientData] build voxel success first.");
                return;
            }

            if (_colliderVoxelTool.graph == null)
            {
                Debug.LogError("[VoxelViewer:SaveClientData] graph is null.请先构造联通数据");
                return;
            }

            string binDirPath = Path.Combine(cvClientDataPath, $"{mapID}", "voxel");
            string jsonDirPath = Path.Combine(cvDebugDataPath, $"{mapID}", "voxel_client_json");
            CreateDir(binDirPath);
            CreateDir(jsonDirPath);

            int regionCellSize = Mathf.CeilToInt(cvRegionSize / cvVoxelSize);
            if (_colliderVoxelTool.SaveClientData(binDirPath, jsonDirPath, regionCellSize))
            {
                Debug.LogFormat(
                    "[VoxelViewer:SaveClientData] save bin data to {0} successfully. save json data to {1} successfully.",
                    binDirPath, jsonDirPath);
            }
            else
            {
                Debug.LogErrorFormat(
                    "[VoxelViewer:SaveClientData] save bin data to {0} failed. save json data to {1} failed.",
                    binDirPath, jsonDirPath);
            }

            GC.Collect();
            AssetDatabase.Refresh();
        }


        [FoldoutGroup("Collider Voxel/导出")]
        [Button("导出服务端碰撞体素数据"), ResponsiveButtonGroup("Collider Voxel/导出/Server")]
        public void ExportServerData()
        {
            GC.Collect();
            if (_colliderVoxelTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelViewer:SaveServerBytes] build voxel success first.");
                return;
            }

            if (!Directory.Exists(csServerDataPath))
            {
                Directory.CreateDirectory(csServerDataPath);
            }

            string bytesFile = Path.Combine(csServerDataPath, $"conf_scene_mask_{mapID}.bytes");
            if (File.Exists(bytesFile))
            {
                File.Delete(bytesFile);
            }

            string debugMapDir = Path.Combine(cvDebugDataPath, $"{mapID}");

            if (!Directory.Exists(debugMapDir))
            {
                Directory.CreateDirectory(debugMapDir);
            }

            string bytesCompareFile = Path.Combine(debugMapDir, "ByteCompareTxt.txt");
            if (File.Exists(bytesCompareFile))
            {
                File.Delete(bytesCompareFile);
            }

            _colliderVoxelTool.SaveServerData(bytesFile, bytesCompareFile);

            GC.Collect();
            AssetDatabase.Refresh();
        }


        [FoldoutGroup("Collider Voxel/提交")]
        [Button("提交")]
        private void Commit()
        {
            ShellUtils.CommitSvn(cvClientDataPath);
            ShellUtils.CommitSvn(csServerDataPath);
            ShellUtils.CommitSvn(cvDebugDataPath);
        }

        #region 碰撞体素调试

        [FoldoutGroup("Collider Voxel/调试")]
        [Button("解析后端碰撞体素二进制数据")]
        public void LoadServerBytes2Txt(int loadMapId)
        {
            string filePath = Path.Combine(csServerDataPath, $"conf_scene_mask_{loadMapId}.bytes");

            if (!File.Exists(filePath))
            {
                Debug.LogErrorFormat("{0} 文件不存在，无法解析，请确定输入的mapID以及是否已经导出二进制数据", filePath);
                return;
            }

            string fileDirPath = Path.Combine(cvDebugDataPath, $"{loadMapId}");

            if (!Directory.Exists(fileDirPath))
            {
                Directory.CreateDirectory(fileDirPath);
            }

            string serverBytes2TxtFile = Path.Combine(fileDirPath, "ServerBytesLoad.txt");
            if (File.Exists(serverBytes2TxtFile))
            {
                File.Delete(serverBytes2TxtFile);
            }

            using (var reader = new BinaryReader(File.Open(filePath, FileMode.Open)))
            {
                using (StreamWriter writer = new StreamWriter(serverBytes2TxtFile, false, Encoding.UTF8))
                {
                    int maxX = reader.ReadInt32();
                    int maxY = reader.ReadInt32();
                    int maxZ = reader.ReadInt32();
                    int totalSpanNum = reader.ReadInt32();
                    writer.WriteLine($"x:{maxX}");
                    writer.WriteLine($"y:{maxY}");
                    writer.WriteLine($"z:{maxZ}");
                    writer.WriteLine($"totalSpansNum:{totalSpanNum}");
                    for (int x = 0; x <= maxX; x++)
                    {
                        for (int z = 0; z <= maxZ; z++)
                        {
                            writer.WriteLine($"x:{x},z:{z},spanNum:{reader.ReadByte()},index:{reader.ReadInt32()}");
                        }
                    }

                    for (int i = 0; i < totalSpanNum; i++)
                    {
                        ulong result = reader.ReadUInt64();
                        int tmpMin = (int)(result >> 48) & 0xffff;
                        int tmpMax = (int)(result >> 32) & 0xffff;
                        int tmpMask = (int)(result >> 24) & 0xff;
                        writer.WriteLine($"index:{i},result:{result},min:{tmpMin},max;{tmpMax},mask:{tmpMask}");
                    }
                }
            }

            Debug.LogFormat("[VoxelViewer:LoadServerBytes2Txt] save data to {0} successfully.", serverBytes2TxtFile);

            GC.Collect();
            AssetDatabase.Refresh();
        }

        #endregion

        #endregion


        #region NavMesh

        private VoxelTool _navMeshTool;

        [FoldoutGroup("Nav Mesh")] [LabelText("NavMesh体素大小(长宽)")] [Range(0.01f, 5.0f)]
        public float nmVoxelSize =
            0.15f;

        [FoldoutGroup("Nav Mesh")] [LabelText("NavMesh体素高度")] [Range(0.01f, 5.0f)]
        public float nmVoxelHeight =
            0.01f;

        [FoldoutGroup("Nav Mesh")] [LabelText("NavMesh体素格子范围"), ReadOnly]
        public Vector3Int nmMaxCell =
            Vector3Int.zero;

        [FoldoutGroup("Nav Mesh")] [LabelText("Agent Height")] [Range(0.0f, 10.0f)]
        public float nmAgentHeight =
            1.0f;

        [FoldoutGroup("Nav Mesh")] [LabelText("Agent Radius")] [Range(0.0f, 10.0f)]
        public float nmAgentRadius =
            0.5f;

        [FoldoutGroup("Nav Mesh")] [LabelText("Climb Height")] [Range(0.0f, 999.0f)]
        public float nmClimbHeight =
            0.5f;

        [FoldoutGroup("Nav Mesh")] [LabelText("Max Slope")] [Range(0.0f, 60.0f)]
        public float nmMaxSlope =
            60.0f;

        [FoldoutGroup("Nav Mesh/过滤")]
        [LabelText("Filter Low Hanging Obstacles"), Tooltip("体素上表面之差小于walkClimb,上面的体素变为可行走,过滤悬空的可走障碍物")]
        public bool nmFilterLowHangingObstacles =
            true;

        [FoldoutGroup("Nav Mesh/过滤")] [LabelText("Filter Ledge Spans"), Tooltip("体素与邻居体素上表面之差超过这个高度，则变为不可走")]
        public bool nmFilterLedgeSpans;

        [FoldoutGroup("Nav Mesh/过滤")]
        [LabelText("Filter Walkable Low Height Spans"), Tooltip("上下体素之间空隙高度小于walkHeight，下体素变为不可行走")]
        public bool nmFilterWalkableLowHeightSpans =
            true;


        [FoldoutGroup("Nav Mesh/优化")] [LabelText("地图中必定可到达的坐标点(世界坐标)")]
        public Vector3Int nmWalkablePoint;

        [FoldoutGroup("Nav Mesh/优化/Debug")] [LabelText("是否开启检查合并封闭空间体素")]
        public bool nmOpenCheckDebug;

        [FoldoutGroup("Nav Mesh/优化/Debug")] [LabelText("检查合并封闭空间体素的范围")]
        public Vector2Int nmCheckSpanValue;

        private GameObject _voxelTopMeshObj;
        private Material _voxelTopMeshMat;
        private Material _navMeshMat;
        private Material _graphMat;
        private Material _nodeMat;

        private void CreateVoxelTopMeshObj()
        {
            if (_voxelTopMeshObj == null)
            {
                GameObject obj = GameObject.Find(VoxelTopMesh);
                if (obj != null)
                {
                    DestroyImmediate(obj);
                }

                _voxelTopMeshObj = new GameObject(VoxelTopMesh);
                GameObjectUtility.SetStaticEditorFlags(_voxelTopMeshObj, StaticEditorFlags.NavigationStatic);
                GameObjectUtility.SetNavMeshArea(_voxelTopMeshObj, NavMesh.GetAreaFromName("Walkable"));
            }

            int childCount = _voxelTopMeshObj.transform.childCount;

            for (int i = childCount - 1; i >= 0; i--)
            {
                Transform child = _voxelTopMeshObj.transform.GetChild(i);
                DestroyImmediate(child.gameObject);
            }
        }

        private Material voxelTopMeshMat
        {
            get
            {
                if (_voxelTopMeshMat == null)
                {
                    _voxelTopMeshMat =
                        AssetDatabase.LoadAssetAtPath<Material>(
                            "Packages/com.nemo.game.editor/Res/Voxel/VoxelTopMesh.mat");
                }

                return _voxelTopMeshMat;
            }
        }

        private Material graphMat
        {
            get
            {
                if (_graphMat == null)
                {
                    _graphMat =
                        AssetDatabase.LoadAssetAtPath<Material>("Packages/com.nemo.game.editor/Res/Voxel/Graph.mat");
                }

                return _graphMat;
            }
        }

        private Material nodeMat
        {
            get
            {
                if (_nodeMat == null)
                {
                    _nodeMat =
                        AssetDatabase.LoadAssetAtPath<Material>("Packages/com.nemo.game.editor/Res/Voxel/Node.mat");
                }

                return _nodeMat;
            }
        }

        private Material navMeshMat
        {
            get
            {
                if (_navMeshMat == null)
                {
                    _navMeshMat =
                        AssetDatabase.LoadAssetAtPath<Material>("Packages/com.nemo.game.editor/Res/Voxel/NavMesh.mat");
                }

                return _navMeshMat;
            }
        }


        [FoldoutGroup("Nav Mesh")]
        [Button("构建体素上表面网格(用于烘焙NavMesh)")]
        public void BuildVoxelTopMesh(bool buildMesh = true)
        {
            GC.Collect();
            _navMeshTool ??= new VoxelTool();

            //
            // Step 1. Initialize build config.
            //
            _navMeshTool.Reset();
            _navMeshTool.SetBuildConfig(nmVoxelSize, nmVoxelHeight, nmAgentHeight, nmClimbHeight, nmAgentRadius,
                nmMaxSlope);
            _navMeshTool.SetBoundBox(0, 0, 0, 0 + xSize, ySize, 0 + zSize);

            _navMeshTool.filterLowHangingObstacles = nmFilterLowHangingObstacles;
            _navMeshTool.filterLedgeSpans = nmFilterLedgeSpans;
            _navMeshTool.filterWalkableLowHeightSpans = nmFilterWalkableLowHeightSpans;
            //这里的walkablePoint是世界坐标，需要转换成体素坐标
            _navMeshTool.point = new Vector3Int(Mathf.FloorToInt(nmWalkablePoint.x / nmVoxelSize),
                Mathf.FloorToInt(nmWalkablePoint.y / nmVoxelHeight),
                Mathf.FloorToInt(nmWalkablePoint.z / nmVoxelSize));
            _navMeshTool.needDebugMergeClosedSpace = nmOpenCheckDebug;
            _navMeshTool.checkMergeAntiSpanMin = nmCheckSpanValue.x;
            _navMeshTool.checkMergeAntiSpanMax = nmCheckSpanValue.y;

            nmMaxCell.Set(Mathf.CeilToInt(xSize / nmVoxelSize) - 1,
                Mathf.CeilToInt(ySize / nmVoxelHeight) - 1,
                Mathf.CeilToInt(zSize / nmVoxelSize) - 1);


            // 1. 生成NavMesh对应的体素数据,收集Mesh数据
            bool addMeshSuccess = _navMeshTool.CollectVoxelMesh(collectMeshInfos);
            if (!addMeshSuccess)
            {
                return;
            }

            // 2. 构建体素
            if (_navMeshTool.BuildVoxel())
            {
                int cellX = -1, cellZ = -1;
                uint maxSpanNum = 0;
                uint totalSpanNum = 0;
                for (int z = 0; z <= nmMaxCell.z; ++z)
                {
                    for (int x = 0; x <= nmMaxCell.x; ++x)
                    {
                        uint spanNum = _navMeshTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
                        if (spanNum == 0)
                            continue;
                        if (spanNum > maxSpanNum)
                        {
                            maxSpanNum = spanNum;
                            cellX = x;
                            cellZ = z;
                        }

                        totalSpanNum += spanNum;
                    }
                }

                Debug.LogFormat("Build Voxel Success. MaxSpanNumInCell: {0} x: {1} z: {2},totalSpan:{3}", maxSpanNum,
                    cellX,
                    cellZ, totalSpanNum);
                //3. 构建体素上表面网格
                if (buildMesh)
                {
                    CreateVoxelTopMeshObj();
                    Mesh[] meshes = BuildVoxelTopMeshArray(totalSpanNum);
                    for (int i = 0; i < meshes.Length; i++)
                    {
                        Mesh mesh = meshes[i];
                        GameObject meshObj = new GameObject(mesh.name)
                        {
                            layer = LayerMask.NameToLayer(NavMeshBakeLayerName)
                        };
                        MeshFilter filter = meshObj.AddComponent<MeshFilter>();
                        filter.mesh = mesh;
                        MeshRenderer meshRenderer = meshObj.AddComponent<MeshRenderer>();
                        Material[] materials = new Material[mesh.subMeshCount];
                        Array.Fill(materials, voxelTopMeshMat);
                        meshRenderer.materials = materials;
                        meshObj.transform.SetParent(_voxelTopMeshObj.transform);
                        GameObjectUtility.SetStaticEditorFlags(meshObj, StaticEditorFlags.NavigationStatic);
                    }
                }
            }
            else
            {
                Debug.LogError("[Build NavMesh] Build Voxel Failed.");
            }

            GC.Collect();
        }

        /// <summary>
        /// 收集已生成体素的上表面，构造对应的Mesh
        /// </summary>
        private Mesh[] BuildVoxelTopMeshArray(uint totalSpanNum)
        {
            int totalTriangleNum = (int)totalSpanNum * 6;
            int meshMaxSubMeshNum = 500;
            int subMeshCount = Mathf.CeilToInt((float)totalTriangleNum / MaxVerticesPerSubMesh); //总共有多少个submesh
            int meshCount = Mathf.CeilToInt((float)subMeshCount / meshMaxSubMeshNum); //创建多少个Mesh Obj
            int eachMeshMaxSpan =
                Mathf.FloorToInt(meshMaxSubMeshNum * MaxVerticesPerSubMesh / 6.0f); //每个Mesh Obj最多又多少个Span
            Mesh[] meshes = new Mesh[meshCount];
            Vector3[] vertices = new Vector3[eachMeshMaxSpan * 4];
            int[] triangles = new int[eachMeshMaxSpan * 6];
            int vertexNum = 0;
            int triangleNum = 0;
            int spanIndex = 0;
            int allSpanIndex = 0;
            int meshIndex = 0;
            for (int z = 0; z <= nmMaxCell.z; ++z)
            {
                for (int x = 0; x <= nmMaxCell.x; ++x)
                {
                    uint spanNum = _navMeshTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
                    if (spanNum == 0) continue;
                    for (int i = 0; i < spanNum; i++)
                    {
                        ushort top = SpanBuffer[i * VoxelTool.SpanElementNum + 1];
                        // point0
                        Vector3 v0 = new Vector3((x + 0) * nmVoxelSize, top * nmVoxelHeight, (z + 0) * nmVoxelSize);
                        vertices[vertexNum] = v0;
                        // point1
                        Vector3 v1 = new Vector3((x + 1) * nmVoxelSize, top * nmVoxelHeight, (z + 1) * nmVoxelSize);
                        vertices[vertexNum + 1] = v1;
                        // point2
                        Vector3 v2 = new Vector3((x + 1) * nmVoxelSize, top * nmVoxelHeight, (z + 0) * nmVoxelSize);
                        vertices[vertexNum + 2] = v2;
                        // point3
                        Vector3 v3 = new Vector3((x + 0) * nmVoxelSize, top * nmVoxelHeight, (z + 1) * nmVoxelSize);
                        vertices[vertexNum + 3] = v3;

                        triangles[triangleNum] = vertexNum + 0;
                        triangles[triangleNum + 1] = vertexNum + 1;
                        triangles[triangleNum + 2] = vertexNum + 2;
                        triangles[triangleNum + 3] = vertexNum + 1;
                        triangles[triangleNum + 4] = vertexNum + 0;
                        triangles[triangleNum + 5] = vertexNum + 3;
                        vertexNum += 4;
                        triangleNum += 6;
                        spanIndex++;
                        allSpanIndex++;
                        if (spanIndex == eachMeshMaxSpan || allSpanIndex == totalSpanNum)
                        {
                            meshes[meshIndex] = GenerateMesh($"VoxelTopMesh_{meshIndex}", vertices, triangles);
                            vertexNum = 0;
                            triangleNum = 0;
                            spanIndex = 0;
                            meshIndex++;
                            Array.Fill(vertices, Vector3.zero);
                            Array.Fill(triangles, 0);
                        }
                    }
                }
            }

            return meshes;
        }


        private Mesh GenerateMesh(string meshName, Vector3[] vertices, int[] triangles, Color[] colors = null)
        {
            // 创建网格对象并设置顶点和三角形数据
            Mesh mesh = new Mesh
            {
                name = meshName,
                indexFormat = vertices.Length > 65536 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            if (colors != null)
            {
                mesh.colors = colors;
            }

            // 更新网格
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }


        [FoldoutGroup("Nav Mesh")]
        [Button("保存上表面网格")]
        public void SaveVoxelTopMesh()
        {
            GC.Collect();
            if (_voxelTopMeshObj == null)
            {
                return;
            }

            MeshFilter[] meshFilters = _voxelTopMeshObj.GetComponentsInChildren<MeshFilter>();
            if (meshFilters == null || meshFilters.Length == 0)
            {
                return;
            }

            string path = Path.Combine(Application.dataPath, $"{mapID}", "VoxelTopMesh");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            foreach (MeshFilter meshFilter in meshFilters)
            {
                if (meshFilter.sharedMesh != null)
                {
                    string savePath = Path.Combine(path, $"{meshFilter.sharedMesh.name}.asset");
                    savePath = FileUtil.GetProjectRelativePath(savePath);
                    AssetDatabase.CreateAsset(meshFilter.sharedMesh, savePath);
                    AssetDatabase.SaveAssets();
                    Debug.Log("VoxelTopMesh saved at: " + savePath);
                }
            }

            AssetDatabase.Refresh();
        }

        [FoldoutGroup("Nav Mesh", false)]
        [Button("清除上表面网格")]
        public void ClearVoxelTopMesh()
        {
            if (_voxelTopMeshObj != null)
            {
                DestroyImmediate(_voxelTopMeshObj);
            }

            _voxelTopMeshObj = null;
        }

        [FoldoutGroup("Nav Mesh")]
        [Button("提取NavMesh烘焙对应网格")]
        public void BuildNavMeshBakeMesh()
        {
            // 获取NavMesh数据
            NavMeshTriangulation navMeshData = NavMesh.CalculateTriangulation();
            if (navMeshData.indices.Length < 3)
            {
                Debug.LogError($"NavMeshExporter ExportScene Error - 场景里没有需要被导出的物体，请先用NavMesh进行Bake。");
                return;
            }

            // 通过上述数据可以构建Mesh或者其他使用顶点和三角形索引的数据结构
            Mesh mesh = GenerateMesh("NavMesh", navMeshData.vertices, navMeshData.indices);
            GameObject obj = new GameObject($"NavMeshBakeMesh");
            MeshFilter meshFilter = obj.AddComponent<MeshFilter>();
            meshFilter.mesh = mesh;
            MeshRenderer meshRenderer = obj.AddComponent<MeshRenderer>();
            Material[] materials = new Material[mesh.subMeshCount];
            Array.Fill(materials, navMeshMat);
            meshRenderer.materials = materials;
        }

        [FoldoutGroup("Nav Mesh")]
        [Button("保存NavMesh烘焙对应网格")]
        public void SaveNavMeshBakeMesh()
        {
            // 获取NavMesh数据
            NavMeshTriangulation navMeshData = NavMesh.CalculateTriangulation();
            if (navMeshData.indices.Length < 3)
            {
                Debug.LogError($"NavMeshExporter ExportScene Error - 场景里没有需要被导出的物体，请先用NavMesh进行Bake。");
                return;
            }

            // 通过上述数据可以构建Mesh或者其他使用顶点和三角形索引的数据结构
            Mesh navMesh = GenerateMesh("NavMesh", navMeshData.vertices, navMeshData.indices);
            string path = Path.Combine(Application.dataPath, $"{mapID}");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            string savePath = Path.Combine(path, "NavMeshBakeMesh.asset");
            savePath = FileUtil.GetProjectRelativePath(savePath);
            AssetDatabase.CreateAsset(navMesh, savePath);
            AssetDatabase.SaveAssets();
            Debug.Log("NavMeshBakeMesh saved at: " + savePath);
            AssetDatabase.Refresh();
        }

        #endregion

        #region Debug调试

        private readonly ObjectPool<GameObject> _walkableCubePool =
            new(CreateWalkableCube, GetCube, ReleaseCube);

        private static GameObject _voxelCubeRoot;
        private static GameObject _voxelWalkableRoot;
        private static Material _voxelCommonMat;
        private static Material _connectMat;

        private static Material voxelCommonMat
        {
            get
            {
                if (_voxelCommonMat == null)
                {
                    _voxelCommonMat =
                        AssetDatabase.LoadAssetAtPath<Material>(
                            "Packages/com.nemo.game.editor/Res/Voxel/VoxelCommon.mat");
                }

                return _voxelCommonMat;
            }
        }

        private static Material connectMat
        {
            get
            {
                if (_connectMat == null)
                {
                    _connectMat =
                        AssetDatabase.LoadAssetAtPath<Material>(
                            "Packages/com.nemo.game.editor/Res/Voxel/Connect.mat");
                }

                return _connectMat;
            }
        }


        private static GameObject voxelCubeRoot
        {
            get
            {
                if (_voxelCubeRoot == null)
                {
                    _voxelCubeRoot = GameObject.Find("VoxelCubeRoot");
                    if (_voxelCubeRoot == null)
                    {
                        _voxelCubeRoot = new GameObject("VoxelCubeRoot");
                    }
                }

                return _voxelCubeRoot;
            }
        }

        private static GameObject voxelWalkableRoot
        {
            get
            {
                if (_voxelWalkableRoot == null)
                {
                    _voxelWalkableRoot = GameObject.Find("WalkableVoxel");
                    if (_voxelWalkableRoot == null)
                    {
                        _voxelWalkableRoot = new GameObject("WalkableVoxel");
                        _voxelWalkableRoot.SetParent(voxelCubeRoot, false);
                    }
                }

                return _voxelWalkableRoot;
            }
        }

        private static GameObject CreateWalkableCube()
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.SetParent(voxelWalkableRoot, false);
            cube.GetComponent<MeshRenderer>().sharedMaterial = voxelCommonMat;
            return cube;
        }


        private static void GetCube(GameObject cube)
        {
            cube.SetActive(true);
        }

        private static void ReleaseCube(GameObject cube)
        {
            cube.SetActive(false);
        }

        private readonly List<GameObject> _usingWalkableCubeList = new();

        private void RemoveAllCube()
        {
            foreach (GameObject o in _usingWalkableCubeList)
            {
                _walkableCubePool.Release(o);
            }

            _usingWalkableCubeList.Clear();
        }


        [FoldoutGroup("调试")]
        [Button("打印目标Span信息")]
        private void GetCellInfo(Vector2Int cell, bool isColliderVoxel = true)
        {
            VoxelTool targetTool = isColliderVoxel ? _colliderVoxelTool : _navMeshTool;
            if (targetTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelBuildTool:GetCellInfo] build voxel success first.");
                return;
            }

            StringBuilder builder = new StringBuilder();
            uint spanNum = targetTool.GetSpans(cell.x, cell.y, SpanBuffer, SpanBufferSize);
            builder.AppendFormat("cx: {0} cz: {1} span num: {2}\n", cell.x, cell.y, spanNum);
            for (int i = 0; i < spanNum; ++i)
            {
                builder.AppendFormat("min: {0} max: {1} mask: {2}\n", SpanBuffer[i * VoxelTool.SpanElementNum + 0],
                    SpanBuffer[i * VoxelTool.SpanElementNum + 1],
                    SpanBuffer[i * VoxelTool.SpanElementNum + 2]);
            }

            Debug.Log(builder);
        }

        [FoldoutGroup("调试")]
        [Button("展示构造的体素")]
        public void ShowSpans(bool isColliderVoxel = true, bool justShowFirst = false)
        {
            VoxelTool targetTool = isColliderVoxel ? _colliderVoxelTool : _navMeshTool;
            if (targetTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelBuildTool:ShowSpans] build voxel success first.");
                return;
            }

            RemoveAllCube();

            float voxelSize = isColliderVoxel ? cvVoxelSize : nmVoxelSize;
            int voxelMaxX = isColliderVoxel ? cvMaxCell.x : nmMaxCell.x;
            int voxelMaxZ = isColliderVoxel ? cvMaxCell.z : nmMaxCell.z;
            for (int z = 0; z <= voxelMaxZ; z++)
            {
                for (int x = 0; x <= voxelMaxX; x++)
                {
                    uint spanNum =
                        _colliderVoxelTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
                    if (spanNum == 0) continue;
                    uint showNum = justShowFirst ? 1 : spanNum;
                    for (int i = 0; i < showNum; i++)
                    {
                        ushort min = SpanBuffer[i * VoxelTool.SpanElementNum],
                            max = SpanBuffer[i * VoxelTool.SpanElementNum + 1],
                            area = SpanBuffer[i * VoxelTool.SpanElementNum + 2];
                        GameObject cube = SetCube(x, z, i, min, max, area, voxelSize);
                        _usingWalkableCubeList.Add(cube);
                    }
                }
            }
        }

        private GameObject SetCube(int x, int z, int spanIndex, ushort min, ushort max, ushort area, float voxelSize,
            Material material = null)
        {
            float logicMin = min * cvVoxelHeight, loginMax = max * cvVoxelHeight;
            GameObject cubeObj = _walkableCubePool.Get();
            cubeObj.GetComponent<MeshRenderer>().sharedMaterial = material == null ? voxelCommonMat : material;
            cubeObj.name = $"{x}_{z}_{spanIndex}_{area}";
            cubeObj.transform.localScale = new Vector3(voxelSize - 0.05f, loginMax - logicMin, voxelSize - 0.05f);
            cubeObj.transform.localPosition = new Vector3((float)(x + 0.5) * voxelSize,
                (float)(logicMin + (loginMax - logicMin) * 0.5), (float)((z + 0.5) * voxelSize));
            return cubeObj;
        }

        [FoldoutGroup("调试")]
        [Button("清除所有Span")]
        private void DestroyAllSpans()
        {
            if (_voxelWalkableRoot != null)
            {
                DestroyImmediate(_voxelWalkableRoot);
            }

            _voxelWalkableRoot = null;
            _usingWalkableCubeList.Clear();
            _walkableCubePool.Clear();
        }

        [FoldoutGroup("调试")]
        [Button("显示所有Span")]
        private void ShowAllSpans()
        {
            voxelCubeRoot.SetActive(true);
        }

        [FoldoutGroup("调试")]
        [Button("隐藏所有Span")]
        private void HideAllSpans()
        {
            voxelCubeRoot.SetActive(false);
        }

        [FoldoutGroup("调试")]
        [Button("显示包围盒内Span")]
        private void ShowCellInBoundingObject(GameObject obj, bool isColliderVoxel = true, bool justShowFirst = false)
        {
            VoxelTool targetTool = isColliderVoxel ? _colliderVoxelTool : _navMeshTool;
            if (targetTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelBuildTool:ShowCellInBoundingObject] build voxel success first.");
                return;
            }

            if (obj == null)
            {
                Debug.LogError("[VoxelBuildTool:ShowCellInBoundingObject] obj is null");
                return;
            }

            RemoveAllCube();
            Renderer r = obj.GetComponent<Renderer>();
            if (r != null)
            {
                float voxelSize = isColliderVoxel ? cvVoxelSize : nmVoxelSize;
                int voxelMaxX = isColliderVoxel ? cvMaxCell.x : nmMaxCell.x;
                int voxelMaxZ = isColliderVoxel ? cvMaxCell.z : nmMaxCell.z;
                int minX = Mathf.Max(Mathf.FloorToInt(r.bounds.min.x / voxelSize), 0);
                int minZ = Mathf.Max(Mathf.FloorToInt(r.bounds.min.z / voxelSize), 0);
                int maxX = Mathf.Min(Mathf.FloorToInt(r.bounds.max.x / voxelSize), voxelMaxX);
                int maxZ = Mathf.Min(Mathf.FloorToInt(r.bounds.max.z / voxelSize), voxelMaxZ);
                ShowCellInBoundingBox(minX, maxX, minZ, maxZ, targetTool, voxelSize, justShowFirst);
            }
            else
            {
                Debug.LogError("[VoxelBuildTool:ShowCellInBoundingObject] obj is not has renderer");
            }
        }

        /// <summary>
        /// 显示包围盒内体素格子
        /// </summary>
        /// <param name="minX"></param>
        /// <param name="maxX"></param>
        /// <param name="minZ"></param>
        /// <param name="maxZ"></param>
        /// <param name="targetTool"></param>
        /// <param name="voxelSize"></param>
        /// <param name="justShowFirst"></param>
        private void ShowCellInBoundingBox(int minX, int maxX, int minZ, int maxZ, VoxelTool targetTool,
            float voxelSize, bool justShowFirst)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    uint spanNum = targetTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
                    if (spanNum == 0) continue;
                    uint showNum = justShowFirst ? 1 : spanNum;
                    for (int i = 0; i < showNum; i++)
                    {
                        ushort min = SpanBuffer[i * VoxelTool.SpanElementNum],
                            max = SpanBuffer[i * VoxelTool.SpanElementNum + 1],
                            area = SpanBuffer[i * VoxelTool.SpanElementNum + 2];
                        GameObject cube = SetCube(x, z, i, min, max, area, voxelSize);
                        _usingWalkableCubeList.Add(cube);
                    }
                }
            }
        }


        private readonly List<GameObject> _tempVoxelCubeList = new();

        public VoxelBuildTool(LineRenderer lineRenderer)
        {
            this._lineRenderer = lineRenderer;
        }

        [FoldoutGroup("调试")]
        [Button("显示指定体素")]
        private void ShowTempSpan(int x, int z, bool isColliderVoxel = true)
        {
            ClearTempSpan();
            VoxelTool targetTool = isColliderVoxel ? _colliderVoxelTool : _navMeshTool;
            if (targetTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelBuildTool:ShowTempSpan] build voxel success first.");
                return;
            }

            float voxelSize = isColliderVoxel ? cvVoxelSize : nmVoxelSize;
            uint spanNum = targetTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
            if (spanNum == 0) return;
            for (int i = 0; i < spanNum; i++)
            {
                ushort min = SpanBuffer[i * VoxelTool.SpanElementNum],
                    max = SpanBuffer[i * VoxelTool.SpanElementNum + 1],
                    area = SpanBuffer[i * VoxelTool.SpanElementNum + 2];
                GameObject cube = SetCube(x, z, i, min, max, area, voxelSize);
                _tempVoxelCubeList.Add(cube);
            }
        }

        [FoldoutGroup("调试")]
        [Button("清除指定体素")]
        private void ClearTempSpan()
        {
            foreach (GameObject o in _tempVoxelCubeList)
            {
                _walkableCubePool.Release(o);
            }

            _tempVoxelCubeList.Clear();
        }

        #endregion
    }
}