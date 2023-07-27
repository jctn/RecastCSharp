using System.Collections;
using System.Collections.Generic;
using System.IO;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RecastSharp
{
    public class MapLoader : MonoBehaviour
    {
        private const string MapResourcePath = "Packages/com.nemo.art.scene-repo/Res/Scene/maps";
        private VoxelBuildTool _buildTool;

        [LabelText("地图ID"), OnValueChanged("OnMapIDChange")]
        public int mapID;

        //地图资源目录
        [BoxGroup("Map"), LabelText("地图资源"), ReadOnly]
        public string mapFullPath;

        private void OnValidate()
        {
            if (_buildTool == null)
            {
                _buildTool = gameObject.GetComponent<VoxelBuildTool>();
            }
        }

        private void OnMapIDChange()
        {
            mapFullPath = $"{MapResourcePath}/{mapID}/FinalScene/{mapID}.unity";
            if (_buildTool != null)
            {
                _buildTool.mapID = mapID;
            }
        }


        [Button("加载地图")]
        private void LoadMap()
        {
            if (string.IsNullOrEmpty(mapFullPath))
            {
                return;
            }

            if (!File.Exists(mapFullPath))
            {
                EditorUtility.DisplayDialog("错误", $"com.nemo.art.scene-repo 不存在{mapID}对应的scene文件，路径：{mapFullPath}",
                    "确定");
                return;
            }

            UnloadMap();
            EditorSceneManager.OpenScene(mapFullPath, OpenSceneMode.Additive);
        }


        [Button("卸载地图")]
        private void UnloadMap()
        {
            int count = SceneManager.sceneCount;
            if (count == 1)
            {
                return;
            }

            for (int i = 1; i < count; i++)
            {
                EditorSceneManager.CloseScene(SceneManager.GetSceneAt(1), true);
            }
        }
    }
}