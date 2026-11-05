using UnityEngine;

public class TabletopReset : MonoBehaviour
{
    public Rigidbody[] objects;
    Vector3[] positions;
    Quaternion[] rotations;

    void Awake()
    {
        positions = new Vector3[objects.Length];
        rotations = new Quaternion[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            positions[i] = objects[i].position;
            rotations[i] = objects[i].rotation;
        }
    }

    public void ResetObjects()
    {
        for (int i = 0; i < objects.Length; i++)
        {
            objects[i].linearVelocity = Vector3.zero;
            objects[i].angularVelocity = Vector3.zero;
            objects[i].position = positions[i];
            objects[i].rotation = rotations[i];
            objects[i].Sleep();
        }
    }

    void OnGUI()
    {
        GUI.Box(new Rect(16, 16, 245, 83), "NERO tabletop sandbox");
        GUI.Label(new Rect(28, 42, 220, 20), "Red / blue blocks - two destination trays");
        if (GUI.Button(new Rect(28, 66, 220, 24), "Reset blocks")) ResetObjects();
    }
}
