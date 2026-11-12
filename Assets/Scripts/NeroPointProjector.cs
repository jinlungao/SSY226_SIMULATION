using UnityEngine;

[DisallowMultipleComponent]
public sealed class NeroPointProjector : MonoBehaviour
{
    [SerializeField] Transform robotBase;
    [SerializeField] float pickSurfaceY = .790f;
    [SerializeField] float placeSurfaceY = .752f;

    public readonly struct SurfacePoint
    {
        public Vector3 WorldPosition { get; }
        public Vector3 BasePosition { get; }

        internal SurfacePoint(Vector3 worldPosition, Vector3 basePosition)
        {
            WorldPosition = worldPosition;
            BasePosition = basePosition;
        }
    }

    internal Frame? CaptureFrame(Camera camera)
    {
        return robotBase && isActiveAndEnabled ? new Frame(camera, robotBase, pickSurfaceY, placeSurfaceY) : null;
    }

    // Snapshot the transforms used for the image so a camera move during inference cannot shift the points.
    internal readonly struct Frame
    {
        readonly Matrix4x4 worldFromClip;
        readonly Matrix4x4 baseFromWorld;
        readonly float pickY;
        readonly float placeY;

        internal Frame(Camera camera, Transform robotBase, float pickY, float placeY)
        {
            worldFromClip = camera.cameraToWorldMatrix * camera.projectionMatrix.inverse;
            baseFromWorld = robotBase.worldToLocalMatrix;
            this.pickY = pickY;
            this.placeY = placeY;
        }

        internal SurfacePoint? Project(int[] point2D, bool isPick)
        {
            if (point2D is not { Length: 2 } ||
                point2D[0] < 0 || point2D[0] > 1000 || point2D[1] < 0 || point2D[1] > 1000)
                return null;

            // Clip space uses [-1, 1] and Y up. The VLM image uses [0, 1000] and Y down.
            float x = point2D[0] / 500f - 1f;
            float y = 1f - point2D[1] / 500f;
            Vector3 near = worldFromClip.MultiplyPoint(new Vector3(x, y, -1f));
            Vector3 deeper = worldFromClip.MultiplyPoint(new Vector3(x, y, 0f));
            var ray = new Ray(near, deeper - near);
            var plane = new Plane(Vector3.up, new Vector3(0f, isPick ? pickY : placeY, 0f));
            if (!plane.Raycast(ray, out float distance) || float.IsInfinity(distance)) return null;

            Vector3 worldPoint = ray.GetPoint(distance);
            return new SurfacePoint(worldPoint, baseFromWorld.MultiplyPoint3x4(worldPoint));
        }
    }

    void OnDrawGizmos()
    {
        if (!isActiveAndEnabled || !TryGetComponent(out NeroVlmClient client)) return;
        var result = client.LastResult;
        if (result == null) return;

        var previousColor = Gizmos.color;
        DrawMarker(result.pick?.Point3D, Color.green);
        DrawMarker(result.place?.Point3D, Color.cyan);
        Gizmos.color = previousColor;
    }

    static void DrawMarker(SurfacePoint? point, Color color)
    {
        if (!point.HasValue) return;

        Vector3 surface = point.Value.WorldPosition;
        Gizmos.color = color;
        Gizmos.DrawWireSphere(surface, .012f);
        Gizmos.DrawLine(surface, surface + Vector3.up * .035f);
    }
}
