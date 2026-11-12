using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class NeroTabletopBuilder
{
    const string Target = "Assets/NeroTabletop.unity";
    const string ReportDir = "Verification";
    static double checkStart;

    static NeroTabletopBuilder()
    {
        EditorApplication.delayCall += () =>
        {
            if (!File.Exists(Target) && !EditorApplication.isPlayingOrWillChangePlaymode
                && SceneManager.GetActiveScene().path == "Assets/NeroSimulation.unity") Build();
            else if (!EditorApplication.isPlayingOrWillChangePlaymode && SceneManager.GetActiveScene().path == Target)
            {
                var robot = GameObject.Find("NERO Arm");
                if (robot && !robot.GetComponent<NeroHoldPose>())
                {
                    robot.AddComponent<NeroHoldPose>();
                    EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
                }
                if (!File.Exists("Verification/presentation-v1.txt")) Polish();
            }
        };
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("NeroCheck", false))
            {
                checkStart = EditorApplication.timeSinceStartup;
                EditorApplication.update += CheckUpdate;
            }
        };
    }

    [MenuItem("NERO/Refresh Scene Preview")]
    public static void Polish()
    {
        if (EditorApplication.isPlaying || SceneManager.GetActiveScene().path != Target) return;
        Mat("Warm tabletop", new Color(.38f, .28f, .18f));
        Mat("White tray", new Color(.62f, .66f, .70f));
        Mat("Studio floor", new Color(.16f, .20f, .25f));
        GameObject.Find("Key Light").GetComponent<Light>().intensity = .85f;
        GameObject.Find("Fill Light").GetComponent<Light>().intensity = .25f;
        RenderSettings.ambientLight = new Color(.28f, .31f, .35f);
        if (SceneView.lastActiveSceneView)
            SceneView.lastActiveSceneView.LookAt(new Vector3(-.04f, .95f, 0), Quaternion.Euler(23, -35, 0), 1.2f);
        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        Directory.CreateDirectory(ReportDir);
        Capture(Camera.main, ReportDir + "/overview.png", 1200, 800);
        Capture(GameObject.Find("Observation Camera - RGB").GetComponent<Camera>(), ReportDir + "/observation.png", 640, 480);
        File.WriteAllText("Verification/presentation-v1.txt", "Updated materials, lights and preview.\n");
    }

    static Material Mat(string name, Color color, float metallic = 0)
    {
        string path = "Assets/TabletopMaterials/" + name + ".mat";
        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!m)
        {
            m = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.color = color;
        m.SetFloat("_Metallic", metallic);
        m.SetFloat("_Glossiness", 0.22f);
        EditorUtility.SetDirty(m);
        return m;
    }

    static GameObject Box(string name, Transform parent, Vector3 pos, Vector3 size, Material material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = material;
        return go;
    }

    static void Tray(string name, Vector3 position, Material material, Transform parent)
    {
        var tray = new GameObject(name);
        tray.transform.SetParent(parent, false);
        tray.transform.localPosition = position;
        Box("Bottom", tray.transform, new Vector3(0, .006f, 0), new Vector3(.22f, .012f, .20f), material);
        Box("Left wall", tray.transform, new Vector3(-.106f, .033f, 0), new Vector3(.008f, .054f, .20f), material);
        Box("Right wall", tray.transform, new Vector3(.106f, .033f, 0), new Vector3(.008f, .054f, .20f), material);
        Box("Front wall", tray.transform, new Vector3(0, .033f, -.096f), new Vector3(.204f, .054f, .008f), material);
        Box("Back wall", tray.transform, new Vector3(0, .033f, .096f), new Vector3(.204f, .054f, .008f), material);
    }

    [MenuItem("NERO/Build Tabletop Scene")]
    public static void Build()
    {
        if (EditorApplication.isPlaying) return;
        if (File.Exists(Target))
        {
            EditorSceneManager.OpenScene(Target);
            return;
        }
        var source = SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == "nero");
        if (!source) throw new InvalidOperationException("Open NeroSimulation with the imported nero robot first.");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        var robot = UnityEngine.Object.Instantiate(source);
        SceneManager.MoveGameObjectToScene(robot, scene);
        robot.name = "NERO Arm";
        robot.transform.position = new Vector3(-.45f, .74f, 0);
        robot.transform.rotation = Quaternion.identity;
        robot.transform.localScale = Vector3.one;

        foreach (var behaviour in robot.GetComponentsInChildren<MonoBehaviour>(true))
            if (behaviour.GetType().Name == "Controller" || behaviour.GetType().Name == "JointControl") behaviour.enabled = false;
        var bodies = robot.GetComponentsInChildren<ArticulationBody>(true);
        int rootCount = 0;
        foreach (var body in bodies)
        {
            bool root = !body.transform.parent || !body.transform.parent.GetComponentInParent<ArticulationBody>();
            body.immovable = root;
            if (root) rootCount++;
            body.useGravity = true;
            body.solverIterations = 20;
            body.solverVelocityIterations = 10;
            var drive = body.xDrive;
            drive.stiffness = body.jointType == ArticulationJointType.PrismaticJoint ? 1200 : 6000;
            drive.damping = body.jointType == ArticulationJointType.PrismaticJoint ? 40 : 180;
            drive.forceLimit = body.jointType == ArticulationJointType.PrismaticJoint ? 15 : 150;
            drive.target = Mathf.Clamp(0, drive.lowerLimit, drive.upperLimit);
            body.xDrive = drive;
        }
        robot.AddComponent<NeroHoldPose>();
        Physics.gravity = new Vector3(0, -9.81f, 0);
        Time.fixedDeltaTime = .01f;
        Directory.CreateDirectory("Assets/TabletopMaterials");
        AssetDatabase.Refresh();
        var wood = Mat("Warm tabletop", new Color(.68f, .54f, .36f));
        var steel = Mat("Graphite steel", new Color(.12f, .16f, .20f), .35f);
        var floor = Mat("Studio floor", new Color(.28f, .33f, .38f));
        var red = Mat("Red block", new Color(.88f, .11f, .10f));
        var blue = Mat("Blue block", new Color(.08f, .36f, .90f));
        var white = Mat("White tray", new Color(.88f, .91f, .93f));
        var yellow = Mat("Yellow tray", new Color(.96f, .66f, .10f));
        var setup = new GameObject("Workspace");
        Box("Floor", setup.transform, new Vector3(0, -.045f, 0), new Vector3(6, .08f, 6), floor);
        var table = new GameObject("Table (1.4 x 1.0 m)");
        table.transform.SetParent(setup.transform, false);
        Box("Tabletop", table.transform, new Vector3(0, .70f, 0), new Vector3(1.4f, .08f, 1), wood);
        foreach (float x in new[] { -.60f, .60f })
            foreach (float z in new[] { -.40f, .40f })
                Box("Steel leg", table.transform, new Vector3(x, .33f, z), new Vector3(.055f, .66f, .055f), steel);
        Box("Front support", table.transform, new Vector3(0, .59f, -.4f), new Vector3(1.20f, .06f, .035f), steel);
        Box("Back support", table.transform, new Vector3(0, .59f, .4f), new Vector3(1.20f, .06f, .035f), steel);
        Tray("Tray A - White", new Vector3(.08f, .74f, .24f), white, setup.transform);
        Tray("Tray B - Yellow", new Vector3(-.18f, .74f, .24f), yellow, setup.transform);
        Tray("Tray C - Blue", new Vector3(-.44f, .74f, .24f), blue, setup.transform);
        var blocks = new GameObject("Movable Blocks");
        blocks.transform.SetParent(setup.transform, false);
        var r = Box("Red Cube", blocks.transform, new Vector3(-.08f, .767f, -.23f), Vector3.one * .05f, red).AddComponent<Rigidbody>();
        var b = Box("Blue Cube", blocks.transform, new Vector3(-.10f, .767f, -.32f), Vector3.one * .05f, blue).AddComponent<Rigidbody>();
        var grip = new PhysicsMaterial("Block friction") { dynamicFriction = .65f, staticFriction = .75f, bounciness = 0 };
        AssetDatabase.CreateAsset(grip, "Assets/TabletopMaterials/Block friction.physicMaterial");
        foreach (var rb in new[] { r, b })
        {
            rb.mass = .08f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.GetComponent<Collider>().sharedMaterial = grip;
        }
        var reset = setup.AddComponent<TabletopReset>();
        reset.objects = new[] { r, b };

        var main = new GameObject("Main Camera");
        main.tag = "MainCamera";
        main.transform.position = new Vector3(1.55f, 1.75f, -1.90f);
        main.transform.LookAt(new Vector3(-.04f, .90f, 0));
        var camera = main.AddComponent<Camera>();
        camera.fieldOfView = 43;
        camera.nearClipPlane = .02f;
        camera.farClipPlane = 30;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.13f, .18f, .24f);
        main.AddComponent<AudioListener>();
        var vision = new GameObject("Observation Camera - RGB");
        vision.transform.position = new Vector3(.05f, 1.9f, -.1f);
        vision.transform.LookAt(new Vector3(.05f, .74f, .05f), Vector3.forward);
        var rgb = vision.AddComponent<Camera>();
        rgb.fieldOfView = 58;
        rgb.nearClipPlane = .02f;
        rgb.farClipPlane = 10;
        rgb.clearFlags = CameraClearFlags.SolidColor;
        rgb.backgroundColor = camera.backgroundColor;
        var texture = new RenderTexture(640, 480, 24) { name = "Observation RGB 640x480" };
        AssetDatabase.CreateAsset(texture, "Assets/ObservationRGB.renderTexture");
        rgb.targetTexture = texture;
        rgb.depth = -1;
        var vlm = new SerializedObject(vision.AddComponent<NeroVlmClient>());
        vlm.FindProperty("observationCamera").objectReferenceValue = rgb;
        vlm.ApplyModifiedPropertiesWithoutUndo();
        var projector = new SerializedObject(vision.AddComponent<NeroPointProjector>());
        projector.FindProperty("robotBase").objectReferenceValue = robot.transform.Find("world/base_link");
        projector.ApplyModifiedPropertiesWithoutUndo();
        var light = new GameObject("Key Light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        light.shadows = LightShadows.Soft;
        light.transform.rotation = Quaternion.Euler(48, -35, 0);
        var fill = new GameObject("Fill Light").AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.intensity = .45f;
        fill.transform.rotation = Quaternion.Euler(35, 145, 0);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(.58f, .62f, .68f);
        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(scene, Target);
        foreach (var old in Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).ToArray())
            if (old != scene && !old.isDirty) EditorSceneManager.CloseScene(old, true);
        if (SceneView.lastActiveSceneView)
        {
            SceneView.lastActiveSceneView.LookAt(new Vector3(-.02f, .85f, 0), Quaternion.Euler(25, -35, 0), 1.9f);
            SceneView.lastActiveSceneView.Repaint();
        }
        Selection.activeGameObject = setup;
        Directory.CreateDirectory(ReportDir);
        File.WriteAllText(ReportDir + "/build.txt", "Built table, 2 dynamic cubes, 2 open trays, fixed robot, overview + RGB camera.\nArticulation bodies: " + bodies.Length + "\nFixed roots: " + rootCount);
        Debug.Log("NERO_TABLETOP_READY: " + Target);
    }

    [MenuItem("NERO/Verify Physics (5 seconds)")]
    public static void Verify()
    {
        if (EditorApplication.isPlaying) return;
        if (SceneManager.GetActiveScene().path != Target) EditorSceneManager.OpenScene(Target);
        SessionState.SetBool("NeroCheck", true);
        EditorApplication.isPlaying = true;
    }

    static void CheckUpdate()
    {
        if (!EditorApplication.isPlaying) { EditorApplication.update -= CheckUpdate; return; }
        if (EditorApplication.timeSinceStartup - checkStart < 5) return;
        EditorApplication.update -= CheckUpdate;
        SessionState.SetBool("NeroCheck", false);
        Directory.CreateDirectory(ReportDir);
        string result = "Physics run for 5 seconds\n";
        bool pass = true;
        foreach (string name in new[] { "Red Cube", "Blue Cube" })
        {
            var rb = GameObject.Find(name).GetComponent<Rigidbody>();
            bool settled = Mathf.Abs(rb.position.y - .765f) < .01f && rb.linearVelocity.magnitude < .03f;
            pass &= settled;
            result += name + ": " + rb.position.ToString("F4") + ", speed=" + rb.linearVelocity.magnitude.ToString("F4") + ", settled=" + settled + "\n";
        }
        var robot = GameObject.Find("NERO Arm");
        result += "Robot root: " + robot.transform.position.ToString("F4") + "\n";
        foreach (var ab in robot.GetComponentsInChildren<ArticulationBody>())
        {
            if (ab.isRoot) { bool fixedBase = ab.immovable && ab.linearVelocity.magnitude < .001f; pass &= fixedBase; result += "Fixed base: " + fixedBase + "\n"; }
            if (ab.dofCount > 0)
            {
                float actual = ab.jointType == ArticulationJointType.RevoluteJoint ? ab.jointPosition[0] * Mathf.Rad2Deg : ab.jointPosition[0];
                float error = Mathf.Abs(actual - ab.xDrive.target);
                bool held = error < (ab.jointType == ArticulationJointType.RevoluteJoint ? 3f : .005f);
                pass &= held;
                result += ab.name + ": actual=" + actual.ToString("F3") + ", target=" + ab.xDrive.target.ToString("F3") + ", held=" + held + ", speed=" + ab.jointVelocity[0].ToString("F3") + "\n";
            }
        }
        result += "PASS=" + pass;
        File.WriteAllText(ReportDir + "/physics-check.txt", result);
        Capture(Camera.main, ReportDir + "/overview.png", 1200, 800);
        Capture(GameObject.Find("Observation Camera - RGB").GetComponent<Camera>(), ReportDir + "/observation.png", 640, 480);
        EditorApplication.isPlaying = false;
        Debug.Log("NERO_PHYSICS_CHECK: " + result);
    }

    static void Capture(Camera cam, string path, int width, int height)
    {
        var old = cam.targetTexture;
        var previous = RenderTexture.active;
        var rt = new RenderTexture(width, height, 24);
        var image = new Texture2D(width, height, TextureFormat.RGB24, false);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        image.Apply();
        File.WriteAllBytes(path, image.EncodeToPNG());
        cam.targetTexture = old;
        RenderTexture.active = previous;
        UnityEngine.Object.DestroyImmediate(image);
        rt.Release();
        UnityEngine.Object.DestroyImmediate(rt);
    }
}
