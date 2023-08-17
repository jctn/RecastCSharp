using System;
using System.Collections.Generic;

namespace RecastSharp
{
    public class MeshData : IDisposable
    {
        private int _vertexNum;
        private float[] _vertices;
        private int _triangleNum;
        private int[] _triangles;
        private byte[] _areas;


        public int vertexNum => _vertexNum;
        public int triangleNum => _triangleNum;

        public float[] vertices => _vertices;
        public int[] triangles => _triangles;

        private const int DefaultCapacity = 2048;
        private int _curVerCap;
        private int _curTriCap;

        public MeshData()
        {
            _curVerCap = DefaultCapacity;
            _curTriCap = DefaultCapacity;
            _vertices = new float[_curVerCap * 3];
            _triangles = new int[_curTriCap * 3];
            _areas = new byte[_curTriCap * 3];
            _vertexNum = 0;
            _triangleNum = 0;
        }

        /// <summary>
        /// 添加网格数据
        /// </summary>
        /// <param name="addVertices"></param>
        /// <param name="addVertexNum"></param>
        /// <param name="addTriangles"></param>
        /// <param name="addTriangleNum"></param>
        /// <param name="area"></param>
        public void AddMesh(float[] addVertices, int addVertexNum, int[] addTriangles, int addTriangleNum, byte area)
        {
            //扩容顶点数据
            if (_vertexNum + addVertexNum > _curVerCap)
            {
                _curVerCap = _vertexNum + addVertexNum > _curVerCap * 2
                    ? (_curVerCap + addVertexNum) * 2
                    : _curVerCap * 2;
                float[] nv = new float[_curVerCap * 3];
                Array.Copy(_vertices, nv, _vertexNum * 3);
                _vertices = nv;
            }

            //复制数据
            Array.Copy(addVertices, 0, _vertices, _vertexNum * 3, addVertexNum * 3);

            //扩容三角形数据
            if (_triangleNum + addTriangleNum > _curTriCap)
            {
                _curTriCap = _triangleNum + addTriangleNum > _curTriCap * 2
                    ? (_curTriCap + addTriangleNum) * 2
                    : _curTriCap * 2;
                int[] nt = new int[_curTriCap * 3];
                byte[] na = new byte[_curTriCap * 3];
                Array.Copy(_triangles, nt, _triangleNum * 3);
                Array.Copy(_areas, na, _triangleNum * 3);
                _triangles = nt;
                _areas = na;
            }

            // 复制三角形数据
            for (int i = 0; i < addTriangleNum; i++)
            {
                _triangles[(_triangleNum + i) * 3 + 0] = _vertexNum + addTriangles[i * 3 + 0];
                _triangles[(_triangleNum + i) * 3 + 1] = _vertexNum + addTriangles[i * 3 + 1];
                _triangles[(_triangleNum + i) * 3 + 2] = _vertexNum + addTriangles[i * 3 + 2];

                _areas[_triangleNum + i] = area;
            }

            _vertexNum += addVertexNum;
            _triangleNum += addTriangleNum;
        }


        public void Release()
        {
            _vertexNum = 0;
            _triangleNum = 0;
            Array.Fill(_vertices, 0);
            Array.Fill(_triangles, 0);
            Array.Fill(_areas, (byte)0);
        }

        public void Dispose()
        {
            _vertexNum = 0;
            _triangleNum = 0;
            _vertices = null;
            _triangles = null;
            _areas = null;
        }
    }
}