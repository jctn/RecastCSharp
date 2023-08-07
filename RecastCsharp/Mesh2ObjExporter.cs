using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RecastSharp
{
    struct ObjMaterial
    {
        public string name;
        public string textureName;
    }

    /// <summary>
    /// 把带网格的MeshFilter导出Obj，便于导出模型
    /// </summary>
    public class Mesh2ObjExporter : ScriptableObject
    {
        private static int vertexOffset = 0;
        private static int normalOffset = 0;
        private static int uvOffset = 0;


        //User should probably be able to change this. It is currently left as an excercise for
        //the reader.
        private static string targetFolder = Path.Combine(Application.dataPath, "CSV2Mesh", "ExportedObj");


        private static void WriteMeshData(StreamWriter sw, MeshFilter mf, Dictionary<string, ObjMaterial> materialList)
        {
            Mesh m = mf.sharedMesh;
            Material[] mats = mf.GetComponent<Renderer>().sharedMaterials;

            sw.WriteLine($"g {mf.name}");
            foreach (Vector3 lv in m.vertices)
            {
                Vector3 wv = mf.transform.TransformPoint(lv);
                //This is sort of ugly - inverting x-component since we're in
                //a different coordinate system than "everyone" is "used to".
                sw.WriteLine($"v {-wv.x} {wv.y} {wv.z}");
            }

            sw.WriteLine();
            foreach (Vector3 lv in m.normals)
            {
                Vector3 wv = mf.transform.TransformDirection(lv);

                sw.WriteLine($"vn {-wv.x} {wv.y} {wv.z}");
            }

            sw.WriteLine();

            foreach (Vector3 v in m.uv)
            {
                sw.WriteLine($"vt {v.x} {v.y}");
            }

            for (int material = 0; material < m.subMeshCount; material++)
            {
                sw.WriteLine();
                sw.WriteLine($"usemtl {mats[material].name}");
                sw.WriteLine($"usemap {mats[material].name}");

                //See if this material is already in the materiallist.
                try
                {
                    ObjMaterial objMaterial = new ObjMaterial();

                    objMaterial.name = mats[material].name;

                    if (mats[material].mainTexture)
                        objMaterial.textureName = AssetDatabase.GetAssetPath(mats[material].mainTexture);
                    else
                        objMaterial.textureName = null;

                    materialList.Add(objMaterial.name, objMaterial);
                }
                catch (ArgumentException)
                {
                    //Already in the dictionary
                }


                int[] triangles = m.GetTriangles(material);
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    //Because we inverted the x-component, we also needed to alter the triangle winding.
                    sw.WriteLine(string.Format("f {1}/{1}/{1} {0}/{0}/{0} {2}/{2}/{2}",
                        triangles[i] + 1 + vertexOffset, triangles[i + 1] + 1 + normalOffset,
                        triangles[i + 2] + 1 + uvOffset));
                }
            }

            vertexOffset += m.vertices.Length;
            normalOffset += m.normals.Length;
            uvOffset += m.uv.Length;
        }

        private static void Clear()
        {
            vertexOffset = 0;
            normalOffset = 0;
            uvOffset = 0;
        }

        private static Dictionary<string, ObjMaterial> PrepareFileWrite()
        {
            Clear();

            return new Dictionary<string, ObjMaterial>();
        }

        private static void MaterialsToFile(Dictionary<string, ObjMaterial> materialList, string folder,
            string filename)
        {
            using (StreamWriter sw = new StreamWriter(Path.Combine(folder, filename + ".mtl")))
            {
                foreach (KeyValuePair<string, ObjMaterial> kvp in materialList)
                {
                    sw.Write("\n");
                    sw.Write("newmtl {0}\n", kvp.Key);
                    sw.Write("Ka  0.6 0.6 0.6\n");
                    sw.Write("Kd  0.6 0.6 0.6\n");
                    sw.Write("Ks  0.9 0.9 0.9\n");
                    sw.Write("d  1.0\n");
                    sw.Write("Ns  0.0\n");
                    sw.Write("illum 2\n");

                    if (kvp.Value.textureName != null)
                    {
                        string destinationFile = kvp.Value.textureName;


                        int stripIndex = destinationFile.LastIndexOf(Path.PathSeparator);

                        if (stripIndex >= 0)
                            destinationFile = destinationFile.Substring(stripIndex + 1).Trim();


                        string relativeFile = destinationFile;

                        destinationFile = folder + Path.PathSeparator + destinationFile;

                        Debug.Log("Copying texture from " + kvp.Value.textureName + " to " + destinationFile);

                        try
                        {
                            //Copy the source file
                            File.Copy(kvp.Value.textureName, destinationFile);
                        }
                        catch
                        {
                        }


                        sw.Write("map_Kd {0}", relativeFile);
                    }

                    sw.Write("\n\n\n");
                }
            }
        }

        private static void MeshToFile(MeshFilter mf, string folder, string filename)
        {
            Dictionary<string, ObjMaterial> materialList = PrepareFileWrite();

            using (StreamWriter sw = new StreamWriter(Path.Combine(folder, filename + ".obj")))
            {
                sw.Write("mtllib ./" + filename + ".mtl\n");
                WriteMeshData(sw, mf, materialList);
            }

            MaterialsToFile(materialList, folder, filename);
        }

        private static void MeshesToFile(MeshFilter[] mf, string folder, string filename)
        {
            Dictionary<string, ObjMaterial> materialList = PrepareFileWrite();

            using (StreamWriter sw = new StreamWriter(Path.Combine(folder, filename + ".obj")))
            {
                sw.Write("mtllib ./" + filename + ".mtl\n");

                for (int i = 0; i < mf.Length; i++)
                {
                    WriteMeshData(sw, mf[i], materialList);
                }
            }

            MaterialsToFile(materialList, folder, filename);
        }

        private static bool CreateTargetFolder()
        {
            try
            {
                Directory.CreateDirectory(targetFolder);
            }
            catch
            {
                EditorUtility.DisplayDialog("Error!", "Failed to create target folder!", "");
                return false;
            }

            return true;
        }

        [MenuItem("GameObject/ExportOBJ/Export whole selection to single OBJ")]
        static void ExportWholeSelectionToSingle()
        {
            if (!CreateTargetFolder())
                return;


            Transform[] selection = Selection.GetTransforms(SelectionMode.Editable | SelectionMode.ExcludePrefab);

            if (selection.Length == 0)
            {
                EditorUtility.DisplayDialog("No source object selected!", "Please select one or more target objects",
                    "");
                return;
            }

            int exportedObjects = 0;

            ArrayList mfList = new ArrayList();

            for (int i = 0; i < selection.Length; i++)
            {
                Component[] meshfilter = selection[i].GetComponentsInChildren(typeof(MeshFilter));

                for (int m = 0; m < meshfilter.Length; m++)
                {
                    exportedObjects++;
                    mfList.Add(meshfilter[m]);
                }
            }

            if (exportedObjects > 0)
            {
                MeshFilter[] mf = new MeshFilter[mfList.Count];

                for (int i = 0; i < mfList.Count; i++)
                {
                    mf[i] = (MeshFilter)mfList[i];
                }

                string filename = EditorSceneManager.GetActiveScene().name + "_" + exportedObjects;

                int stripIndex = filename.LastIndexOf(Path.PathSeparator);

                if (stripIndex >= 0)
                    filename = filename.Substring(stripIndex + 1).Trim();

                MeshesToFile(mf, targetFolder, filename);

                EditorUtility.DisplayDialog("Objects exported",
                    "Exported " + exportedObjects + " objects to " + filename, "确认");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            else
                EditorUtility.DisplayDialog("Objects not exported",
                    "Make sure at least some of your selected objects have mesh filters!", "");
        }


        [MenuItem("GameObject/ExportOBJ/Export each selected to single OBJ")]
        static void ExportEachSelectionToSingle()
        {
            if (!CreateTargetFolder())
                return;

            Transform[] selection = Selection.GetTransforms(SelectionMode.Editable | SelectionMode.ExcludePrefab);

            if (selection.Length == 0)
            {
                EditorUtility.DisplayDialog("No source object selected!", "Please select one or more target objects",
                    "");
                return;
            }

            int exportedObjects = 0;


            for (int i = 0; i < selection.Length; i++)
            {
                Component[] meshfilter = selection[i].GetComponentsInChildren(typeof(MeshFilter));

                MeshFilter[] mf = new MeshFilter[meshfilter.Length];

                for (int m = 0; m < meshfilter.Length; m++)
                {
                    exportedObjects++;
                    mf[m] = (MeshFilter)meshfilter[m];
                }

                MeshesToFile(mf, targetFolder, selection[i].name + "_" + i);
            }

            if (exportedObjects > 0)
            {
                EditorUtility.DisplayDialog("Objects exported", "Exported " + exportedObjects + " objects", "");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            else
            {
                EditorUtility.DisplayDialog("Objects not exported",
                    "Make sure at least some of your selected objects have mesh filters!", "");
            }
        }
    }
}