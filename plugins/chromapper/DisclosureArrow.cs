using UnityEngine;
using UnityEngine.UI;

namespace BsDownmap.ChroMapper
{
    internal sealed class DisclosureArrow : MaskableGraphic
    {
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            var rect = GetPixelAdjustedRect();
            mesh.AddVert(new Vector3(rect.xMin, rect.yMax), color, Vector2.zero);
            mesh.AddVert(new Vector3(rect.xMax, rect.yMax), color, Vector2.zero);
            mesh.AddVert(new Vector3(rect.center.x, rect.yMin), color, Vector2.zero);
            mesh.AddTriangle(0, 1, 2);
        }
    }
}
