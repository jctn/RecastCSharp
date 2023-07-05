using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LitJson;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace RecastSharp
{
    public class VoxelBuildTool : MonoBehaviour
    {
        private static readonly uint SpanBufferSize = 32;
        private static readonly ushort[] SpanBuffer = new ushort[SpanBufferSize * 3];

        [LabelText("Map ID")] public int mapID = 10000;
        [LabelText("x size")] [Range(0, 5000)] public float xSize = 500.0f;

        [LabelText("y size")] [Range(0, 1000)] public float ySize = 200.0f;

        [LabelText("z size")] [Range(0, 5000)] public float zSize = 400.0f;

        [LabelText("Agent Height")] [Range(0.0f, 10.0f)]
        public float agentHeight = 2.0f;

        [LabelText("Agent Radius")] [Range(0.0f, 10.0f)]
        public float agentRadius = 0.1f;

        [LabelText("Climb Height")] [Range(0.0f, 999.0f)]
        public float climbHeight = 0.5f;

        [LabelText("Max Slope")] [Range(0.0f, 90.0f)]
        public float maxSlope = 60.0f;

        #region Voxel

        private VoxelTool mVoxelTool = null;

        [FoldoutGroup("Voxel")] [LabelText("Cell Size")] [Range(0.01f, 5.0f)]
        public float cellSize = 0.5f;

        [FoldoutGroup("Voxel")] [LabelText("Cell Height")] [Range(0.01f, 5.0f)]
        public float cellHeight = 0.1f;

        [FoldoutGroup("Voxel")] [LabelText("Min Cell")]
        public Vector3Int minCell = Vector3Int.zero;

        [FoldoutGroup("Voxel")] [LabelText("Max Cell")]
        public Vector3Int maxCell = Vector3Int.zero;


        [FoldoutGroup("Voxel")] [LabelText("Filter Low Hanging Obstacles")]
        public bool filterLowHangingObstacles = true;

        [FoldoutGroup("Voxel")] [LabelText("Filter Ledge Spans")]
        public bool filterLedgeSpans = false;

        [FoldoutGroup("Voxel")] [LabelText("Filter Walkable Low Height Spans")]
        public bool filterWalkableLowHeightSpans = true;

        [FoldoutGroup("Voxel")] [LabelText("Shoal Thresold")] [Range(0.0f, 10.0f)]
        public float shoalThresold = 1.0f;

        [FoldoutGroup("Voxel")] [LabelText("Region Size")]
        public ushort regionSize = 128;

        [FoldoutGroup("Voxel")]
        [LabelText("Client Data Path")]
        [FolderPath(ParentFolder = "Assets", AbsolutePath = true)]
        public string clientDataPath = Path.Combine(Application.dataPath, "MapData", "Client");

        [FoldoutGroup("Voxel")]
        [LabelText("Server Data Path")]
        [FolderPath(ParentFolder = "Assets", AbsolutePath = true)]
        public string serverDataPath = Path.Combine(Application.dataPath, "MapData", "Server");

        [FoldoutGroup("Voxel")]
        [Button("Build Voxel")]
        public void BuildVoxel()
        {
            // 设置体素生成参数
            if (mVoxelTool == null)
            {
                mVoxelTool = new VoxelTool();
            }

            mVoxelTool.Reset();
            mVoxelTool.SetBuildConfig(cellSize, cellHeight, agentHeight, climbHeight, agentRadius, maxSlope);
            mVoxelTool.SetBoundBox(0, 0, 0, xSize, ySize, zSize);
            mVoxelTool.filterLowHangingObstacles = filterLowHangingObstacles;
            mVoxelTool.filterLedgeSpans = filterLedgeSpans;
            mVoxelTool.filterWalkableLowHeightSpans = filterWalkableLowHeightSpans;

            minCell.Set(0, 0, 0);
            maxCell.Set(Mathf.CeilToInt(xSize / cellSize) - 1, Mathf.CeilToInt(ySize / cellHeight) - 1,
                Mathf.CeilToInt(zSize / cellSize) - 1);

            GameObject root = GameObject.Find("Root");
            if (root == null)
            {
                Debug.Log("Cannot find map root.");
                return;
            }

            // collect meshes
            CollectBuildVoxelMeshes(mVoxelTool, root);

            if (mVoxelTool.BuildVoxel())
            {
                int cellx = -1, cellz = -1, maxSpanNum = -1;
                int totalSpanNum = 0;
                //TODO：默认min都是0，0，0
                StringBuilder sb = new StringBuilder();
                for (int z = minCell.z; z <= maxCell.z; ++z)
                {
                    for (int x = minCell.x; x <= maxCell.x; ++x)
                    {
                        int spanNum = mVoxelTool.GetSpans(x, z, SpanBuffer, SpanBufferSize);
                        if (spanNum > maxSpanNum)
                        {
                            maxSpanNum = spanNum;
                            cellx = x;
                            cellz = z;
                        }

                        totalSpanNum += spanNum;
                        if (spanNum > 0)
                        {
                            sb.AppendFormat("cell: {0} {1} smin: {2}，smax: {3}，mask: {4}，area:{5}\n", x, z,
                                SpanBuffer[0],
                                SpanBuffer[1], SpanBuffer[2], SpanBuffer[3]);
                        }
                    }
                }

                File.WriteAllText(Path.Combine(Application.dataPath, "VoxelBuildTool.txt"), sb.ToString(),
                    Encoding.UTF8);
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


        private void CollectBuildVoxelMeshes(VoxelTool tool, GameObject rootObj)
        {
            //TODO:临时处理拿地表的网格
            GameObject root = GameObject.Find("Terrain");
            MeshFilter[] mfs = root.GetComponentsInChildren<MeshFilter>();
            foreach (MeshFilter meshFilter in mfs)
            {
                if (meshFilter.gameObject.activeSelf && meshFilter.gameObject.activeInHierarchy &&
                    meshFilter.sharedMesh != null)
                {
                    if (AddVoxelMesh(tool, meshFilter, 0))
                    {
                        Debug.LogFormat("add mesh {0} success", meshFilter.gameObject.name);
                    }
                    else
                    {
                        Debug.LogErrorFormat("add mesh {0} failed", meshFilter.gameObject.name);
                    }
                }
            }
        }

        private bool AddVoxelMesh(VoxelTool tool, MeshFilter meshFilter, ushort mask)
        {
            List<float> vertexList = new List<float>();
            Mesh mesh = meshFilter.sharedMesh;
            foreach (Vector3 vertex in mesh.vertices)
            {
                Vector3 globalVertex = meshFilter.transform.TransformPoint(vertex);
                vertexList.Add(globalVertex.x);
                vertexList.Add(globalVertex.y);
                vertexList.Add(globalVertex.z);
            }

            return tool.AddMesh(vertexList.ToArray(), vertexList.Count / 3, mesh.triangles, mesh.triangles.Length / 3,
                0,
                mask);
        }

        [FoldoutGroup("Voxel")]
        [Button("Save Voxel Json Data")]
        public void SaveVoxelJsonData()
        {
            if (mVoxelTool is not { buildSuccess: true })
            {
                Debug.LogError("[VoxelViewer:SaveClientData] build voxel success first.");
                return;
            }

            //导出Json文件，分块尺寸为64*64(米)
            string dirPath = Path.Combine(Application.dataPath, $"{mapID}_VoxelData");
            if (AssetDatabase.IsValidFolder(dirPath))
            {
                AssetDatabase.DeleteAsset(dirPath);
                AssetDatabase.Refresh();
            }

            Directory.CreateDirectory(dirPath);
            int regionWidth = 64;
            int regionHeight = 64;
            int regionWidthNum = Mathf.CeilToInt(xSize / regionWidth);
            int regionHeightNum = Mathf.CeilToInt(zSize / regionHeight);
            int endRegionWidth = (int)(xSize % regionWidth);
            int endRegionHeight = (int)(zSize % regionHeight);
            int normalCellXNum = Mathf.CeilToInt(regionWidth / cellSize);
            int normalCellZNum = Mathf.CeilToInt(regionHeight / cellSize);
            for (int i = 0; i < regionWidthNum; i++)
            {
                bool isEndWidthRegion = i == regionWidthNum - 1;
                for (int j = 0; j < regionHeightNum; j++)
                {
                    bool isEndHeightRegion = j == regionHeightNum - 1;
                    int rWidth = isEndWidthRegion ? endRegionWidth : regionWidth;
                    int rHeight = isEndHeightRegion ? endRegionHeight : regionHeight;
                    RegionVoxelData regionVoxelData = new RegionVoxelData();
                    regionVoxelData.startCellX = minCell.x + i * normalCellXNum;
                    regionVoxelData.startCellZ = minCell.z + j * normalCellZNum;
                    regionVoxelData.endCellX = regionVoxelData.startCellX + Mathf.CeilToInt(rWidth / cellSize);
                    regionVoxelData.endCellZ = regionVoxelData.startCellZ + Mathf.CeilToInt(rHeight / cellSize);
                    int cellNum = (regionVoxelData.endCellZ - regionVoxelData.startCellZ) *
                                  (regionVoxelData.endCellX - regionVoxelData.startCellX);
                    regionVoxelData.cellSpanInfos = new CellSpanInfo[cellNum];
                    int cellIndex = 0;
                    for (int k = regionVoxelData.startCellX; k < regionVoxelData.endCellX; k++)
                    {
                        for (int l = regionVoxelData.startCellZ; l < regionVoxelData.endCellZ; l++)
                        {
                            int spanNum = mVoxelTool.GetSpans(k, l, sSpanBuffer, sSpanBufferSize);
                            Debug.Log($"spanNum-->{spanNum},{k},{l}");
                            if (spanNum < 0)
                            {
                                Debug.Log($"IsBuildSuccess = {mVoxelTool.IsBuildSuccess()}");
                            }

                            CellSpanInfo info = new CellSpanInfo(k, l, spanNum);
                            if (spanNum > 0)
                            {
                                for (int m = 0; m < spanNum; m++)
                                {
                                    ushort min = sSpanBuffer[m * 3];
                                    ushort max = sSpanBuffer[m * 3 + 1];
                                    ushort mask = sSpanBuffer[m * 3 + 2];
                                    info.spans[m] = new VoxelSpan(min, max, mask);
                                }
                            }

                            regionVoxelData.cellSpanInfos[cellIndex] = info;
                            cellIndex++;
                        }
                    }

                    File.WriteAllText(Path.Combine(dirPath, "region_" + i + "_" + j + ".json"),
                        JsonMapper.ToJson(regionVoxelData));
                }

                GC.Collect();
            }
        }

        [FoldoutGroup("Voxel")]
        [Button("Save Client Data")]
        public void SaveClientData()
        {
            if (mVoxelTool == null || !mVoxelTool.buildSuccess)
            {
                Debug.LogError("[VoxelViewer:SaveClientData] build voxel success first.");
                return;
            }

            // string dirPath = clientDataPath;
            string dirPath = Path.Combine(Application.dataPath, $"{mapID}_client");
            if (AssetDatabase.IsValidFolder(dirPath))
            {
                AssetDatabase.DeleteAsset(dirPath);
                AssetDatabase.Refresh();
            }

            Directory.CreateDirectory(dirPath);
            AssetDatabase.Refresh();

            if (mVoxelTool.SaveClientData(dirPath))
            {
                AssetDatabase.Refresh();
                Debug.LogFormat("[VoxelViewer:SaveClientData] save data to {0} successfully.", dirPath);
            }
            else
            {
                Debug.LogErrorFormat("[VoxelViewer:SaveClientData] save data to {0} failed.", dirPath);
            }
        }

        #endregion
    }
}