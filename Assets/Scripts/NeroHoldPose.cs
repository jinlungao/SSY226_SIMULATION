using System.IO;
using UnityEngine;

// Holds the imported configuration until an external joint controller is connected.
[DefaultExecutionOrder(1000)]
public class NeroHoldPose : MonoBehaviour
{
    void Start()
    {
        // Imported convex hulls overlap at joints. Self-collision is disabled in
        // this starter scene; contacts with the table and blocks remain enabled.
        var colliders = GetComponentsInChildren<Collider>();
        for (int i = 0; i < colliders.Length; i++)
            for (int j = i + 1; j < colliders.Length; j++)
                Physics.IgnoreCollision(colliders[i], colliders[j]);
        string info = "Initial articulation coordinates\n";
        foreach (var body in GetComponentsInChildren<ArticulationBody>())
        {
            if (body.dofCount == 0) continue;
            var drive = body.xDrive;
            float position = body.jointPosition[0];
            drive.target = body.jointType == ArticulationJointType.RevoluteJoint ? position * Mathf.Rad2Deg : position;
            drive.targetVelocity = 0;
            drive.driveType = body.jointType == ArticulationJointType.RevoluteJoint
                ? ArticulationDriveType.Target : ArticulationDriveType.Force;
            if (body.jointType == ArticulationJointType.PrismaticJoint)
            {
                drive.stiffness = 200;
                drive.damping = 10;
            }
            body.xDrive = drive;
            info += body.name + ": initial=" + position + ", target=" + drive.target + "\n";
        }
        Directory.CreateDirectory("Verification");
        File.WriteAllText("Verification/initial-joints.txt", info);
    }
}
