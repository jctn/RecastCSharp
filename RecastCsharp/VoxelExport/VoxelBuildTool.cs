using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Nemo.GameLogic.Editor;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEditor.AI;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Pool;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;

namespace RecastSharp
{
    [Serializable]
    public class CollectMeshInfo
    {
        [LabelText("网格根节点")] public GameObject Target;
        [LabelText("网格是否有LOD")] public Boolean HasLod;
    }

    public class VoxelBuildTool : MonoBehaviour
    {
        private static readonly uint SpanBufferSize = 32;
        private static readonly ushort[] SpanBuffer = new ushort[SpanBufferSize * VoxelTool.SpanElementNum];
        private const string NavMeshGenName = "NavMeshGen";
        private const string NavMeshSurfaceName = "NavMeshSurface";

        private const string NavMeshBakeLayerName = "NavMeshBake";

        //GameEditor中间数据目录
        private const string DefaultGameEditorResPath = "../../packages/game/com.nemo.game.editor/Res/SceneEditor/Map";

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
        public List<CollectMeshInfo> CollectMeshInfos;


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
        public bool cvFilterLedgeSpans = false;

        [FoldoutGroup("Collider Voxel/过滤")]
        [LabelText("Filter Walkable Low Height Spans"), Tooltip("上下体素之间空隙高度小于walkHeight，下体素变为不可行走")]
        public bool cvFilterWalkableLowHeightSpans = true;

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

        private void OpenClientFolder()
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

        private void OpenServerFolder()
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

        private void OpenDebugFolder()
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
            _colliderVoxelTool.SetBoundBox(0, 0, 0, xSize, ySize, zSize);
            _colliderVoxelTool.filterLowHangingObstacles = cvFilterLowHangingObstacles;
            _colliderVoxelTool.filterLedgeSpans = cvFilterLedgeSpans;
            _colliderVoxelTool.filterWalkableLowHeightSpans = cvFilterWalkableLowHeightSpans;


            cvMaxCell.Set(Mathf.CeilToInt(xSize / cvVoxelSize) - 1, Mathf.CeilToInt(ySize / cvVoxelHeight) - 1,
                Mathf.CeilToInt(zSize / cvVoxelSize) - 1);


            // 收集Mesh数据
            if (CollectMeshInfos == null || CollectMeshInfos.Count == 0)
            {
                EditorUtility.DisplayDialog("错误", "CollectMeshInfos为空，请设置正确的列表数据", "关闭");
                return;
            }

            foreach (var info in CollectMeshInfos)
            {
                if (info.Target == null)
                    continue;
                CollectSceneMesh(info.Target, info.HasLod, _colliderVoxelTool);
            }

            if (_colliderVoxelTool.BuildVoxel())
            {
                int cellx = -1, cellz = -1, maxSpanNum = -1;
                int totalSpanNum = 0;
                for (int x = 0; x <= cvMaxCell.x; ++x)
                {
                    for (int z = 0; z <= cvMaxCell.z; ++z)
                    {
                        int spanNum = _colliderVoxelTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
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

        /// <summary>
        /// 收集场景Mesh
        /// </summary>
        /// <param name="root"></param>
        /// <param name="hasLod"></param>
        /// <param name="tool"></param>
        private void CollectSceneMesh(GameObject root, bool hasLod, VoxelTool tool)
        {
            if (root == null) return;
            if (hasLod)
            {
                LODGroup[] lodGroups = root.GetComponentsInChildren<LODGroup>();
                foreach (LODGroup group in lodGroups)
                {
                    LOD lod = group.GetLODs()[0];
                    foreach (Renderer r in lod.renderers)
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        TryAddMesh(mf, tool);
                    }
                }
            }
            else
            {
                MeshFilter[] mfs = root.GetComponentsInChildren<MeshFilter>();
                foreach (MeshFilter meshFilter in mfs)
                {
                    TryAddMesh(meshFilter, tool);
                }
            }
        }


        private void TryAddMesh(MeshFilter mf, VoxelTool tool)
        {
            if (mf.gameObject.activeSelf && mf.gameObject.activeInHierarchy &&
                mf.sharedMesh != null)
            {
                AddVoxelMesh(tool, mf, 0);
            }
        }

        private void AddVoxelMesh(VoxelTool tool, MeshFilter meshFilter, ushort mask)
        {
            Mesh mesh = meshFilter.sharedMesh;
            int vertexCount = mesh.vertices.Length;
            float[] vertices = new float[vertexCount * 3];
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 vertex = mesh.vertices[i];
                Vector3 globalVertex = meshFilter.transform.TransformPoint(vertex);
                vertices[i * 3] = globalVertex.x;
                vertices[i * 3 + 1] = globalVertex.y;
                vertices[i * 3 + 2] = globalVertex.z;
            }

            tool.AddMesh(vertices, vertexCount, mesh.triangles, mesh.triangles.Length / 3, 0);
        }

        [FoldoutGroup("Collider Voxel/导出")]
        [Button("导出体素完整Json数据(二次处理/查数据)")]
        public void ExportFullJsonData()
        {
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
            if (_colliderVoxelTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelViewer:SaveClientData] build voxel success first.");
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

        [FoldoutGroup("Collider Voxel/导出")]
        [Button("导出服务端碰撞体素数据(旧)"), ResponsiveButtonGroup("Collider Voxel/导出/Server")]
        public void ExportServerDataTmp()
        {
            if (_colliderVoxelTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelViewer:SaveServerBytes] build voxel success first.");
                return;
            }

            //生成服务端二进制数据
            ExportServerByteFile();
            //生成服务端二进制对比文本数据（按照二进制的写入顺序来写入txt）
            ExportServerByteCompareTxt();
            GC.Collect();
            AssetDatabase.Refresh();
        }

        private void ExportServerByteFile()
        {
            if (!Directory.Exists(csServerDataPath))
            {
                Directory.CreateDirectory(csServerDataPath);
            }

            string filePath = Path.Combine(csServerDataPath, $"conf_scene_mask_{mapID}_old.bytes");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            using (var writer = new BinaryWriter(File.Open(filePath, FileMode.OpenOrCreate)))
            {
                writer.Write(cvMaxCell.x);
                writer.Write(cvMaxCell.y);
                writer.Write(cvMaxCell.z);
                int totalSpansNum = _colliderVoxelTool.GetTotalSpanCount();
                int zero = 0;

                int dataIndex = 0;
                writer.Write(totalSpansNum);
                for (int z = 0; z <= cvMaxCell.z; z++)
                {
                    for (int x = 0; x <= cvMaxCell.x; x++)
                    {
                        int spanNum = _colliderVoxelTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
                        writer.Write((byte)spanNum);
                        if (spanNum > 0)
                        {
                            writer.Write(dataIndex);
                            dataIndex += spanNum;
                        }
                        else
                        {
                            writer.Write(zero);
                        }
                    }
                }

                for (int z = 0; z <= cvMaxCell.z; z++)
                {
                    for (int x = 0; x <= cvMaxCell.x; x++)
                    {
                        int spanNum = _colliderVoxelTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
                        if (spanNum == 0) continue;
                        for (int i = 0; i < spanNum; i++)
                        {
                            // ushort min = SpanBuffer[i * VoxelTool.SpanElementNum];
                            // ushort max = SpanBuffer[i * VoxelTool.SpanElementNum + 1];
                            // ushort mask = SpanBuffer[i * VoxelTool.SpanElementNum + 2];
                            //临时处理
                            ushort min = SpanBuffer[i * VoxelTool.SpanElementNum + 1];
                            ushort max = (ushort)cvMaxCell.y;
                            ushort mask = 1;
                            ulong result = ((ulong)min << 48) | ((ulong)max << 32) | ((ulong)mask << 24);
                            writer.Write(result);
                        }
                    }
                }
            }

            Debug.LogFormat("[VoxelViewer:SaveServerBytes] save data to {0} successfully.", filePath);
        }

        private void ExportServerByteCompareTxt()
        {
            string fileDirPath = Path.Combine(cvDebugDataPath, $"{mapID}");

            if (!Directory.Exists(fileDirPath))
            {
                Directory.CreateDirectory(fileDirPath);
            }

            string filePath = Path.Combine(fileDirPath, "ByteCompareTxtOld.txt");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            using (StreamWriter writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                writer.WriteLine($"x:{cvMaxCell.x}");
                writer.WriteLine($"y:{cvMaxCell.y}");
                writer.WriteLine($"z:{cvMaxCell.z}");
                int totalSpansNum = _colliderVoxelTool.GetTotalSpanCount();
                int dataIndex = 0;
                writer.WriteLine($"totalSpansNum:{totalSpansNum}");
                for (int z = 0; z <= cvMaxCell.z; z++)
                {
                    for (int x = 0; x <= cvMaxCell.x; x++)
                    {
                        int spanNum = _colliderVoxelTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
                        if (spanNum > 0)
                        {
                            writer.WriteLine($"x:{x},z:{z},spanNum:{spanNum},index:{dataIndex}");
                            dataIndex += spanNum;
                        }
                        else
                        {
                            writer.WriteLine($"x:{x},z:{z},spanNum:0,index:0");
                        }
                    }
                }

                int index = 0;
                for (int z = 0; z <= cvMaxCell.z; z++)
                {
                    for (int x = 0; x <= cvMaxCell.x; x++)
                    {
                        int spanNum = _colliderVoxelTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
                        if (spanNum == 0) continue;
                        for (int i = 0; i < spanNum; i++)
                        {
                            // ushort min = SpanBuffer[i * VoxelTool.SpanElementNum];
                            // ushort max = SpanBuffer[i * VoxelTool.SpanElementNum + 1];
                            // ushort mask = SpanBuffer[i * VoxelTool.SpanElementNum + 2];
                            //临时处理
                            ushort min = SpanBuffer[i * VoxelTool.SpanElementNum + 1];
                            ushort max = (ushort)cvMaxCell.y;
                            ushort mask = 1;
                            ulong result = ((ulong)min << 48) | ((ulong)max << 32) | ((ulong)mask << 24);
                            writer.WriteLine($"index:{index++},result:{result},min:{min},max:{max}");
                        }
                    }
                }
            }

            Debug.LogFormat("[VoxelViewer:ExportServerByteCompareTxt] save data to {0} successfully.", filePath);
        }


        [FoldoutGroup("Collider Voxel/提交")]
        [Button("提交")]
        private void Commit()
        {
            ShellUtils.CommitSvn(cvClientDataPath);
            ShellUtils.CommitSvn(csServerDataPath);
            ShellUtils.CommitSvn(cvDebugDataPath);
        }

        #region Debug调试

        private readonly ObjectPool<GameObject> _walkableCubePool =
            new(CreateWalkableCube, GetCube, ReleaseCube, DestroyCube);

        private static GameObject _voxelCubeRoot;
        private static GameObject _voxelWalkableRoot;
        private static Material _voxelShoalMat;

        private static Material voxelWalkableMat
        {
            get
            {
                if (_voxelShoalMat == null)
                {
                    _voxelShoalMat =
                        AssetDatabase.LoadAssetAtPath<Material>(
                            "Packages/com.nemo.game.editor/Res/Voxel/VoxelCommon.mat");
                }

                return _voxelShoalMat;
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
            cube.GetComponent<MeshRenderer>().sharedMaterial = voxelWalkableMat;
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

        private static void DestroyCube(GameObject cube)
        {
            DestroyImmediate(cube);
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

        [FoldoutGroup("Collider Voxel/调试")]
        [Button("解析后端二进制数据")]
        public void LoadServerBytes2Txt(int loadMapId)
        {
            string filePath = Path.Combine(csServerDataPath, $"conf_scene_mask_{loadMapId}.bytes");

            if (!File.Exists(filePath))
            {
                Debug.LogErrorFormat("{0} 文件不存在，无法解析，请确定输入的mapID以及是否已经导出二进制数据", filePath);
                return;
            }

            string fileDirPath = Path.Combine(cvDebugDataPath, $"{mapID}");

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

        [FoldoutGroup("Collider Voxel/调试")]
        [Button("测试Raycast")]
        private void MyTest(Vector2 startPos, Vector2 endPos, float voxelSize = 1, bool disAdd = false,
            bool isMax = true)
        {
            Vector2 direction = (endPos - startPos).normalized;
            float distance = Vector2.Distance(startPos, endPos);

            Vector2Int currentVoxel =
                new Vector2Int(Mathf.FloorToInt(startPos.x / voxelSize), Mathf.FloorToInt(startPos.y / voxelSize));
            Vector2Int step = new Vector2Int((int)Mathf.Sign(direction.x), (int)Mathf.Sign(direction.y));

            float tMaxX, tMaxY;
            float tDeltaX, tDeltaY;
        
            // 计算tMax和tDelta
            // 如果direction某一轴为0，说明射线永远到达那个方向的体素
            if (direction.x != 0)
            {
                tMaxX = ((currentVoxel.x + (step.x > 0 ? 1 : 0)) * voxelSize - startPos.x) /
                        direction.x;
                tDeltaX = Mathf.Abs(voxelSize / direction.x);
            }
            else
            {
                tMaxX = float.MaxValue;
                tDeltaX = float.MaxValue;
            }

            if (direction.y != 0)
            {
                tMaxY = ((currentVoxel.y + (step.y > 0 ? 1 : 0)) * voxelSize - startPos.y) /
                        (direction.y);
                tDeltaY = Mathf.Abs(voxelSize / direction.y);
            }
            else
            {
                tMaxY = float.MaxValue;
                tDeltaY = float.MaxValue;
            }

            Debug.LogFormat("<color=green>info-->tDeltaX:{0},tDeltaY:{1},distance:{2},step:{3},direction{4}</color>",
                tDeltaX, tDeltaY, distance, step, direction);
            float currentDistance = 0f;
            while (currentDistance < distance)
            {
                currentDistance = Mathf.Min(tMaxX, tMaxY);
                Vector2 hitPos = startPos + currentDistance * direction;
                Vector2Int hitPosVoxel = new Vector2Int(Mathf.FloorToInt(hitPos.x / voxelSize),
                    Mathf.FloorToInt(hitPos.y / voxelSize));
                Debug.LogFormat(
                    "<color=yellow>before-->currentVoxel.x:{0},currentVoxel.y:{1},tMaxX:{2},tMaxY:{3},currentDistance:{4},pos:{5},pos_voxel:{6}</color>",
                    currentVoxel.x, currentVoxel.y, tMaxX, tMaxY,
                    currentDistance, hitPos, hitPosVoxel);
                // 找出最小的tMax，确定下一个体素的边
                if (tMaxX < tMaxY)
                {
                    tMaxX += tDeltaX;
                    currentVoxel.x += step.x;
                }
                else
                {
                    tMaxY += tDeltaY;
                    currentVoxel.y += step.y;
                }

                Debug.LogFormat(
                    "<color=red>after-->currentVoxel.x:{0},currentVoxel.y:{1},tMaxX:{2},tMaxY:{3},currentDistance:{4},pos:{5},pos_voxel:{6}</color>",
                    currentVoxel.x, currentVoxel.y, tMaxX, tMaxY,
                    currentDistance, hitPos, hitPosVoxel);
            }
        }

        [FoldoutGroup("Collider Voxel/调试")]
        [Button("打印目标Span信息")]
        private void GetCellInfo(Vector2Int cell)
        {
            if (_colliderVoxelTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelViewer:GetCellInfo] build voxel success first.");
                return;
            }

            StringBuilder builder = new StringBuilder();
            int spanNum = _colliderVoxelTool.GetSpans(cell.x, cell.y, SpanBuffer, SpanBufferSize);
            builder.AppendFormat("cx: {0} cz: {1} span num: {2}\n", cell.x, cell.y, spanNum);
            for (int i = 0; i < spanNum; ++i)
            {
                builder.AppendFormat("min: {0} max: {1} mask: {2}\n", SpanBuffer[i * VoxelTool.SpanElementNum + 0],
                    SpanBuffer[i * VoxelTool.SpanElementNum + 1],
                    SpanBuffer[i * VoxelTool.SpanElementNum + 2]);
            }

            Debug.Log(builder);
        }

        [FoldoutGroup("Collider Voxel/调试")]
        [Button("展示构造的体素"), ResponsiveButtonGroup("Collider Voxel/调试/Spans")]
        private void ShowSpans()
        {
            if (_colliderVoxelTool is not { buildVoxelSuccess: true })
            {
                Debug.LogError("[VoxelViewer:ShowSpans] build voxel success first.");
                return;
            }

            RemoveAllCube();
            for (int x = 0; x <= cvMaxCell.x; x++)
            {
                for (int z = 0; z <= cvMaxCell.z; z++)
                {
                    int spanNum = _colliderVoxelTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
                    if (spanNum == 0) continue;
                    for (int i = 0; i < spanNum; i++)
                    {
                        ushort min = SpanBuffer[i * VoxelTool.SpanElementNum],
                            max = SpanBuffer[i * VoxelTool.SpanElementNum + 1],
                            area = SpanBuffer[i * VoxelTool.SpanElementNum + 2];
                        GameObject cube = SetCube(x, z, i, min, max, area);
                        _usingWalkableCubeList.Add(cube);
                    }
                }
            }
        }

        private GameObject SetCube(int x, int z, int spanIndex, ushort min, ushort max, ushort area)
        {
            float logicMin = min * cvVoxelHeight, loginMax = max * cvVoxelHeight;
            GameObject cubeObj = _walkableCubePool.Get();
            cubeObj.name = $"{x}_{z}_{spanIndex}_{area}";
            cubeObj.transform.localScale = new Vector3(cvVoxelSize - 0.05f, loginMax - logicMin, cvVoxelSize - 0.05f);
            cubeObj.transform.localPosition = new Vector3((float)(x + 0.5) * cvVoxelSize,
                (float)(logicMin + (loginMax - logicMin) * 0.5), (float)((z + 0.5) * cvVoxelSize));
            return cubeObj;
        }

        [FoldoutGroup("Collider Voxel/调试")]
        [Button("清除所有Span"), ResponsiveButtonGroup("Collider Voxel/调试/Spans")]
        private void DestroyAllSpans()
        {
            RemoveAllCube();
            _walkableCubePool.Clear();
        }

        [FoldoutGroup("Collider Voxel/调试")]
        [Button("显示所有Span"), ResponsiveButtonGroup("Collider Voxel/调试/Spans")]
        private void ShowAllSpans()
        {
            voxelCubeRoot.SetActive(true);
        }

        [FoldoutGroup("Collider Voxel/调试")]
        [Button("隐藏所有Span"), ResponsiveButtonGroup("Collider Voxel/调试/Spans")]
        private void HideAllSpans()
        {
            voxelCubeRoot.SetActive(false);
        }


        private readonly List<GameObject> _tempVoxelCubeList = new();

        [FoldoutGroup("Collider Voxel/调试")]
        [Button("显示指定体素")]
        private void ShowTempSpan(int x, int z)
        {
            ClearTempSpan();

            int spanNum = _colliderVoxelTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
            if (spanNum == 0) return;
            for (int i = 0; i < spanNum; i++)
            {
                ushort min = SpanBuffer[i * VoxelTool.SpanElementNum],
                    max = SpanBuffer[i * VoxelTool.SpanElementNum + 1],
                    area = SpanBuffer[i * VoxelTool.SpanElementNum + 2];
                GameObject cube = SetCube(x, z, i, min, max, area);
                _tempVoxelCubeList.Add(cube);
            }
        }

        [FoldoutGroup("Collider Voxel/调试")]
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

        #endregion


        #region NavMesh

        private VoxelTool _navMeshTool;

        [FoldoutGroup("Nav Mesh")] [LabelText("NavMesh体素大小(长宽)")] [Range(0.01f, 5.0f)]
        public float nmVoxelSize = 0.15f;

        [FoldoutGroup("Nav Mesh")] [LabelText("NavMesh体素高度")] [Range(0.01f, 5.0f)]
        public float nmVoxelHeight = 0.01f;

        [FoldoutGroup("Nav Mesh")] [LabelText("NavMesh体素格子范围"), ReadOnly]
        public Vector3Int nmMaxCell = Vector3Int.zero;

        [FoldoutGroup("Nav Mesh")] [LabelText("Agent Height")] [Range(0.0f, 10.0f)]
        public float nmAgentHeight = 1.0f;

        [FoldoutGroup("Nav Mesh")] [LabelText("Agent Radius")] [Range(0.0f, 10.0f)]
        public float nmAgentRadius = 0f;

        [FoldoutGroup("Nav Mesh")] [LabelText("Climb Height")] [Range(0.0f, 999.0f)]
        public float nmClimbHeight = 0.5f;

        [FoldoutGroup("Nav Mesh")] [LabelText("Max Slope")] [Range(0.0f, 60.0f)]
        public float nmMaxSlope = 60.0f;

        [FoldoutGroup("Nav Mesh/过滤")]
        [LabelText("Filter Low Hanging Obstacles"), Tooltip("体素上表面之差小于walkClimb,上面的体素变为可行走,过滤悬空的可走障碍物")]
        public bool nmFilterLowHangingObstacles = true;

        [FoldoutGroup("Nav Mesh/过滤")] [LabelText("Filter Ledge Spans"), Tooltip("体素与邻居体素上表面之差超过这个高度，则变为不可走")]
        public bool nmFilterLedgeSpans = false;

        [FoldoutGroup("Nav Mesh/过滤")]
        [LabelText("Filter Walkable Low Height Spans"), Tooltip("上下体素之间空隙高度小于walkHeight，下体素变为不可行走")]
        public bool nmFilterWalkableLowHeightSpans = true;


        private GameObject _navMeshGenObj;
        private GameObject _navMeshSurfaceObj;
        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private NavMeshSurface _meshSurface;
        private Material _navMeshMat;

        private void CreateNavMeshObj()
        {
            if (_navMeshGenObj == null)
            {
                GameObject obj = GameObject.Find(NavMeshGenName);
                if (obj != null)
                {
                    DestroyImmediate(obj);
                }

                _navMeshGenObj = new GameObject(NavMeshGenName)
                {
                    layer = LayerMask.NameToLayer(NavMeshBakeLayerName)
                };
                _meshFilter = _navMeshGenObj.AddComponent<MeshFilter>();
                _meshRenderer = _navMeshGenObj.AddComponent<MeshRenderer>();
                _meshRenderer.sharedMaterial = navMeshMat;
                GameObjectUtility.SetStaticEditorFlags(_navMeshGenObj, StaticEditorFlags.NavigationStatic);
                GameObjectUtility.SetNavMeshArea(_navMeshGenObj, NavMesh.GetAreaFromName("Walkable"));
            }

            if (_navMeshSurfaceObj == null)
            {
                GameObject obj = GameObject.Find(NavMeshSurfaceName);
                if (obj != null)
                {
                    DestroyImmediate(obj);
                }

                _navMeshSurfaceObj = new GameObject(NavMeshSurfaceName);
                _meshSurface = _navMeshSurfaceObj.AddComponent<NavMeshSurface>();
                _meshSurface.collectObjects = CollectObjects.All;
                _meshSurface.layerMask = LayerMask.GetMask(NavMeshBakeLayerName); // 调整为适合实际情况的图层
                _meshSurface.onPreUpdate = OnPreUpdate;
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
        public void GenVoxelTopMesh()
        {
            if (_navMeshTool == null)
            {
                _navMeshTool = new VoxelTool();
            }

            //
            // Step 1. Initialize build config.
            //
            _navMeshTool.Reset();
            _navMeshTool.SetBuildConfig(nmVoxelSize, nmVoxelHeight, nmAgentHeight, nmClimbHeight, nmAgentRadius,
                nmMaxSlope);
            _navMeshTool.SetBoundBox(0, 0, 0, xSize, ySize, zSize);

            _navMeshTool.filterLowHangingObstacles = nmFilterLowHangingObstacles;
            _navMeshTool.filterLedgeSpans = nmFilterLedgeSpans;
            _navMeshTool.filterWalkableLowHeightSpans = nmFilterWalkableLowHeightSpans;

            nmMaxCell.Set(Mathf.CeilToInt(xSize / nmVoxelSize) - 1, Mathf.CeilToInt(ySize / nmVoxelHeight) - 1,
                Mathf.CeilToInt(zSize / nmVoxelSize) - 1);

            // 1. 生成NavMesh对应的体素数据
            // 收集Mesh数据
            if (CollectMeshInfos == null || CollectMeshInfos.Count == 0)
            {
                EditorUtility.DisplayDialog("错误", "CollectMeshInfos为空，请设置正确的列表数据", "关闭");
                return;
            }

            foreach (var info in CollectMeshInfos)
            {
                if (info.Target == null)
                    continue;
                CollectSceneMesh(info.Target, info.HasLod, _navMeshTool);
            }

            if (_navMeshTool.BuildVoxel())
            {
                // 2. 创建 GameObject，并设置网格数据
                CreateNavMeshObj();
                Mesh surfaceMesh = BuildVoxelTopMesh();
                _meshFilter.mesh = surfaceMesh;
                Material[] materials = new Material[surfaceMesh.subMeshCount];
                Array.Fill(materials, navMeshMat);
                _meshRenderer.materials = materials;
            }
            else
            {
                Debug.LogError("[Build NavMesh] Build Voxel Failed.");
            }

            GC.Collect();
        }

        private void OnPreUpdate(ref NavMeshBuildSettings obj)
        {
            obj.agentClimb = nmClimbHeight;
            obj.agentHeight = nmAgentHeight;
            obj.agentRadius = nmAgentRadius;
            obj.agentSlope = nmMaxSlope;
        }

        [FoldoutGroup("Nav Mesh")]
        [Button("构建NavMesh")]
        public void BuildNavMesh()
        {
            if (_meshSurface != null)
            {
                _meshSurface.BuildNavMesh();
            }
        }

        [FoldoutGroup("Nav Mesh")]
        [Button("烘焙NavMesh数据")]
        public void BakeNavMesh()
        {
            if (_meshSurface != null)
            {
                NavMeshAssetManager.instance.StartBakingSurfaces(new Object[] { _meshSurface });
            }
        }


        /// <summary>
        /// 收集已生成体素的上表面，构造对应的Mesh
        /// </summary>
        private Mesh BuildVoxelTopMesh()
        {
            int totalSpanNum = 0;
            for (int z = 0; z < nmMaxCell.z; ++z)
            {
                for (int x = 0; x < nmMaxCell.x; ++x)
                {
                    int spanNum = _navMeshTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
                    if (spanNum == 0) continue;
                    totalSpanNum += spanNum;
                }
            }

            Vector3[] vertices = new Vector3[totalSpanNum * 4];
            int[] triangles = new int[totalSpanNum * 6];
            int vertexNum = 0;
            int triangleNum = 0;
            for (int z = 0; z < nmMaxCell.z; ++z)
            {
                for (int x = 0; x < nmMaxCell.x; ++x)
                {
                    int spanNum = _navMeshTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
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
                    }
                }
            }

            // 创建网格对象并设置顶点和三角形数据
            Mesh mesh = new Mesh
            {
                indexFormat = vertices.Length > 65536 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.SetVertices(vertices);
            // 创建子网格
            int maxVerticesPerSubMesh = 65535;
            int subMeshCount = Mathf.CeilToInt((float)triangles.Length / maxVerticesPerSubMesh);
            mesh.subMeshCount = subMeshCount;
            for (int i = 0; i < subMeshCount; i++)
            {
                // 计算当前子网格的索引范围
                int startIndex = i * maxVerticesPerSubMesh;
                int endIndex = Mathf.Min((i + 1) * maxVerticesPerSubMesh, triangles.Length);
                int triangleCount = endIndex - startIndex;

                // 创建子网格的索引数组
                int[] subMeshIndices = new int[triangleCount];
                Array.Copy(triangles, startIndex, subMeshIndices, 0, triangleCount);

                // 设置子网格的索引
                mesh.SetTriangles(subMeshIndices, i);
            }

            // 更新网格
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        #endregion
    }
}