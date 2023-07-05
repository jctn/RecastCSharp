using System;

namespace RecastSharp
{
    public class MeshData : IDisposable
    {
        private const int VertexCapacity = 1 << 10;
        private const int TriangleCapacity = 1 << 10;

        private int _vertexNum = 0;
        private int _vertexCapacity = VertexCapacity;
        private float[] _vertexes = null;
        private int _triangleNum = 0;
        private int _triangleCapacity = TriangleCapacity;
        private int[] _triangles = null;
        private byte[] _areas = null;
        private ushort[] _masks = null;


        public int vertexNum => _vertexNum;
        public int triangleNum => _triangleNum;

        public float[] vertexes => _vertexes;
        public int[] triangles => _triangles;
        
        public ushort[] masks => _masks;

        public MeshData()
        {
            _vertexes = new float[_vertexCapacity * 3];
            _triangles = new int[_triangleCapacity * 3];
            _areas = new byte[_triangleCapacity];
            _masks = new ushort[_triangleCapacity];
        }


        public bool AddMesh(float[] vertexes, int aVertexNum, int[] aTriangles, int aTriangleNum, byte area,
            ushort mask)
        {
            // 复制顶点数据
            if (_vertexNum + aVertexNum > _vertexCapacity && !ExpandVertexCapacity(_vertexNum + aVertexNum))
            {
                return false;
            }

            Array.Copy(vertexes, 0, this._vertexes, _vertexNum * 3, aVertexNum * 3);

            if (_triangleNum + aTriangleNum > _triangleCapacity && !ExpandTriangleCapacity(_triangleNum + aTriangleNum))
            {
                return false;
            }

            // 复制三角形数据
            for (int i = 0; i < aTriangleNum; i++)
            {
                _triangles[(_triangleNum + i) * 3 + 0] = _vertexNum + aTriangles[i * 3 + 0];
                _triangles[(_triangleNum + i) * 3 + 1] = _vertexNum + aTriangles[i * 3 + 1];
                _triangles[(_triangleNum + i) * 3 + 2] = _vertexNum + aTriangles[i * 3 + 2];

                _areas[_triangleNum + i] = area;
                _masks[_triangleNum + i] = mask;
            }

            _vertexNum += aVertexNum;
            _triangleNum += aTriangleNum;
            return true;
        }

        private bool ExpandVertexCapacity(int toNum)
        {
            int curNum = _vertexCapacity;
            while (curNum < toNum)
            {
                curNum <<= 1;
                // 已达到最大容纳量
                if (curNum < VertexCapacity)
                {
                    return false;
                }
            }

            Array.Resize(ref _vertexes, curNum * 3);
            _vertexCapacity = curNum;
            return true;
        }

        private bool ExpandTriangleCapacity(int toNum)
        {
            int curNum = _triangleCapacity;
            while (curNum < toNum)
            {
                curNum <<= 1;
                // 已达到最大容纳量
                if (curNum < TriangleCapacity)
                {
                    return false;
                }
            }

            Array.Resize(ref _triangles, curNum * 3);
            Array.Resize(ref _areas, curNum);
            Array.Resize(ref _masks, curNum);
            _triangleCapacity = curNum;
            return true;
        }

        public void Dispose()
        {
            _vertexNum = 0;
            _vertexCapacity = VertexCapacity;
            _vertexes = null;
            _triangleNum = 0;
            _triangleCapacity = TriangleCapacity;
            _triangles = null;
            _areas = null;
            _masks = null;
        }
    }
}