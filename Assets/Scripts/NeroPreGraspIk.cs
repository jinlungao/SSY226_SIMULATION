using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

[DisallowMultipleComponent]
public sealed class NeroPreGraspIk : MonoBehaviour
{
    [SerializeField] Transform robotBase;
    [SerializeField] Transform tcp;
    [SerializeField] BoxCollider redCube;
    [SerializeField] ArticulationBody finger1;
    [SerializeField] ArticulationBody finger2;
    [SerializeField, Min(.01f)] float clearance = .05f;

    // Seed near the elbow-up, fingers-down solution for this tabletop layout.
    static readonly float[] TabletopSeed = { 56f, 24f, -9f, 98f, -147f, -37f, -48f };
    const float RotationWeight = .18f;
    const float PositionTolerance = .01f;
    const float RotationTolerance = 5f;
    const float GraspDepth = .032f;
    const float ClosingDuration = .8f;
    // For the scene's 50 mm cube and pads, first contact is near 48 mm per finger.
    const float GripTarget = .044f;
    enum Phase { Idle, Approach, Descend, Close, Lift, RaiseForTransfer, TransferMidpoint, Transfer, AlignForRelease, LowerToTrayMidpoint, LowerToTray, Release, Retreat, ReplayApproach, Replay, AwaitPlan }

    [System.Serializable]
    sealed class PlanRequest
    {
        public Vector3 start;
        public Vector3 goal;
    }

    [System.Serializable]
    sealed class ReplayTrajectory
    {
        public float dt;
        public Vector3[] points;
    }

    readonly List<ArticulationBody> joints = new List<ArticulationBody>();
    readonly Vector3[] pivots = new Vector3[7];
    readonly Vector3[] axes = new Vector3[7];
    readonly Vector3[] movedPivots = new Vector3[7];
    readonly Vector3[] movedAxes = new Vector3[7];
    readonly float[] start = new float[7];
    readonly float[] goal = new float[7];
    readonly float[] minimum = new float[7];
    readonly float[] maximum = new float[7];
    readonly float[,] jacobian = new float[6, 7];
    readonly float[,] system = new float[6, 7];

    Vector3 initialTcp;
    Quaternion initialRotation;
    Vector3 target;
    Quaternion targetRotation;
    bool hasTarget;
    Phase phase;
    bool preGraspReached;
    bool cubeHeld;
    bool automatic;
    bool useLearnedTransfer;
    NeroTrajectoryRecorder demonstrationRecorder;
    string demonstrationDataset;
    NeroVlmClient.Target selectedPick;
    NeroVlmClient.Target selectedPlace;
    BoxCollider ActiveCube => selectedPick?.SceneCollider ? selectedPick.SceneCollider : redCube;
    Vector3 localizedPick;
    UnityWebRequest pendingPlan;
    ReplayTrajectory replay;
    float replayMaxError;
    float replayMaxAngle;
    BoxCollider trayFloor;
    Vector3 trayApproach;
    Vector3 trayRelease;
    Vector3 trayReleaseApproach;
    Vector3 transferStart;
    Quaternion transferStartRotation;
    Vector3 liftTarget;
    Vector3 cubeAtClosure;
    Vector3 cubeOffsetAtLiftStart;
    float finger1Start;
    float finger2Start;
    float elapsed;
    float duration;
    string status = "Ready";
    PhysicsMaterial padMaterial;
    BoxCollider pad1, pad2;

    void Awake()
    {
        for (Transform link = tcp; link && link != robotBase; link = link.parent)
        {
            if (link.TryGetComponent(out ArticulationBody joint) &&
                joint.jointType == ArticulationJointType.RevoluteJoint)
                joints.Add(joint);
        }
        joints.Reverse();

        if (!tcp || !finger1 || !finger2) return;
        padMaterial = new PhysicsMaterial("NERO fingertip pads")
        {
            staticFriction = .95f,
            dynamicFriction = .85f,
            frictionCombine = PhysicsMaterialCombine.Maximum
        };
        pad1 = AddGripPad(finger1, 1f);
        pad2 = AddGripPad(finger2, -1f);
    }

    BoxCollider AddGripPad(ArticulationBody finger, float inward)
    {
        // Flat contact surface on the inner side of the simulated fingertip.
        var contact = finger.gameObject.AddComponent<BoxCollider>();
        Quaternion toFinger = Quaternion.Inverse(finger.transform.rotation) * tcp.rotation;
        contact.center = toFinger * new Vector3(inward * .017f, -.019f, 0f);
        Vector3 size = toFinger * new Vector3(.012f, .032f, .036f);
        contact.size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
        contact.sharedMaterial = padMaterial;
        return contact;
    }

    void OnDestroy()
    {
        if (padMaterial) Destroy(padMaterial);
    }

    void OnDisable()
    {
        if (demonstrationRecorder) demonstrationRecorder.CompleteTransferCapture(false);
        StopAllCoroutines();
        pendingPlan?.Dispose();
        pendingPlan = null;
        phase = Phase.Idle;
        automatic = false;
    }

    public bool CanBeginAutomatic => isActiveAndEnabled && phase == Phase.Idle && !cubeHeld;

    public void BeginPickAndPlace(NeroVlmClient.LocalizationResult result)
    {
        if (!CanBeginAutomatic) return;
        if (result?.status != "ok" || result.pick?.ExecutionProfile != "cube" ||
            result.place?.ExecutionProfile != "tray" || result.place.relation != "in")
        {
            status = "Automatic mode currently places cubes inside trays.";
            return;
        }
        var cube = result.pick.SceneCollider;
        var floor = result.place.SceneCollider;
        if (!cube || !floor || !result.pick.Point3D.HasValue || !result.place.Point3D.HasValue)
        {
            status = "Pick or place is missing a collider or projected point.";
            return;
        }
        localizedPick = result.pick.Point3D.Value.WorldPosition;
        Vector3 localizedPlace = result.place.Point3D.Value.WorldPosition;
        if (!NearFootprint(localizedPick, cube.bounds) || !NearFootprint(localizedPlace, floor.bounds))
        {
            status = "VLM point missed the cube or tray. Take a new image.";
            Debug.LogWarning($"NERO automatic: VLM points {localizedPick.ToString("F3")} / {localizedPlace.ToString("F3")} missed their objects.", this);
            return;
        }
        selectedPick = result.pick;
        selectedPlace = result.place;
        automatic = true;
        useLearnedTransfer = true;
        BeginMove();
    }

    void BeginDemonstration(string cubeId, string cubeName, string trayId, string trayName, string datasetName)
    {
        var cube = GameObject.Find($"Workspace/Movable Blocks/{cubeName}")?.GetComponent<BoxCollider>();
        var floor = GameObject.Find($"Workspace/{trayName}/Bottom")?.GetComponent<BoxCollider>();
        if (!cube || !floor)
        {
            status = "Demonstration cube or tray is missing.";
            return;
        }

        selectedPick = new NeroVlmClient.Target { id = cubeId, SceneCollider = cube };
        selectedPlace = new NeroVlmClient.Target { id = trayId, SceneCollider = floor };
        localizedPick = cube.bounds.center;
        automatic = true;
        useLearnedTransfer = false;
        demonstrationRecorder = GetComponent<NeroTrajectoryRecorder>();
        demonstrationDataset = datasetName;
        BeginMove();
    }

    static bool NearFootprint(Vector3 point, Bounds bounds)
    {
        const float pixelProjectionMargin = .02f;
        return Mathf.Abs(point.x - bounds.center.x) <= bounds.extents.x + pixelProjectionMargin &&
               Mathf.Abs(point.z - bounds.center.z) <= bounds.extents.z + pixelProjectionMargin;
    }

    Vector3 CubeTop()
    {
        if (automatic)
        {
            // The projected point selects the object; use its simulated collider to refine the grasp center.
            Bounds cube = ActiveCube.bounds;
            return new Vector3(cube.center.x, cube.max.y, cube.center.z);
        }
        return redCube.transform.TransformPoint(redCube.center + Vector3.up * (redCube.size.y * .5f));
    }

    void BeginMove()
    {
        if (!robotBase || !tcp || !tcp.IsChildOf(robotBase) || !ActiveCube || !finger1 || !finger2 || joints.Count != 7)
        {
            status = "Check TCP, Robot Base, red cube, and both finger references.";
            return;
        }

        Vector3 cubeTop = CubeTop();
        // The TCP's local +Y points from the wrist toward the fingertips.
        targetRotation = robotBase.rotation * Quaternion.Euler(0f, 0f, 180f);
        preGraspReached = false;
        cubeHeld = false;
        if (!PlanMove(cubeTop + robotBase.up * clearance, Phase.Approach, useTabletopSeed: true)) return;
        SetTarget(finger1, .05f);
        SetTarget(finger2, -.05f);
    }

    void BeginPlace()
    {
        trayFloor = selectedPlace?.SceneCollider;
        if (!trayFloor && selectedPlace == null)
            trayFloor = GameObject.Find("Workspace/Tray A - White/Bottom")?.GetComponent<BoxCollider>();
        if (!trayFloor)
        {
            status = "Selected tray floor not found.";
            return;
        }

        Vector3 up = robotBase.up;
        Bounds floor = trayFloor.bounds;
        Vector3 floorTop = new Vector3(floor.center.x, floor.max.y, floor.center.z);
        float cubeBottomOffset = ActiveCube.bounds.min.y - tcp.position.y;
        // The blue tray is close to the base and needs a landing point farther out.
        Vector3 towardRobot = Vector3.ProjectOnPlane(robotBase.position - floorTop, up).normalized;
        float landingOffset = selectedPlace?.LandingOffset ?? .065f;
        if (selectedPlace?.id == "blue_tray" && Mathf.Approximately(landingOffset, 0f))
            landingOffset = -.05f;
        trayRelease = floorTop + towardRobot * landingOffset + up * (.008f - cubeBottomOffset);
        trayReleaseApproach = trayRelease + up * .09f;
        // Keep the learned transfer endpoint at the tray center. Apply the
        // outward offset during the local alignment, before lowering the cube.
        trayApproach = selectedPlace?.id == "blue_tray" ? trayReleaseApproach - towardRobot * landingOffset : trayReleaseApproach;
        Debug.Log($"NERO place: {selectedPlace?.id ?? "white_tray"}, landing offset {landingOffset * 1000f:F0} mm, " +
            $"base release {robotBase.InverseTransformPoint(trayRelease).ToString("F4")}.", this);
        float rise = Vector3.Dot(trayApproach - tcp.position, up);
        PlanMove(tcp.position + up * Mathf.Max(0f, rise), Phase.RaiseForTransfer, useTabletopSeed: false);
    }

    bool PlanTransfer(Vector3 destination, Phase nextPhase)
    {
        Vector3 from = Vector3.ProjectOnPlane(transferStart - robotBase.position, robotBase.up);
        Vector3 to = Vector3.ProjectOnPlane(destination - robotBase.position, robotBase.up);
        float yaw = Vector3.SignedAngle(from, to, robotBase.up);
        targetRotation = Quaternion.AngleAxis(yaw, robotBase.up) * transferStartRotation;
        return PlanMove(destination, nextPhase, useTabletopSeed: false);
    }

    bool VerifyPlacement()
    {
        Bounds cube = ActiveCube.bounds;
        Bounds floor = trayFloor.bounds;
        const float wallThickness = .008f;
        bool inside = cube.min.x >= floor.min.x + wallThickness && cube.max.x <= floor.max.x - wallThickness &&
                      cube.min.z >= floor.min.z + wallThickness && cube.max.z <= floor.max.z - wallThickness;
        float heightError = Mathf.Abs(cube.min.y - floor.max.y);
        bool settled = ActiveCube.attachedRigidbody && ActiveCube.attachedRigidbody.linearVelocity.sqrMagnitude < .0001f;
        string destination = selectedPlace?.id.Replace('_', ' ') ?? "white tray";
        bool passed = inside && heightError < .005f && settled;
        status = passed ? $"Place passed: cube in {destination}" : "Place failed: cube not settled in tray";
        Debug.Log($"NERO place: {status}, floor error {heightError * 1000f:F1} mm, center offset {Vector2.Distance(new Vector2(cube.center.x, cube.center.z), new Vector2(floor.center.x, floor.center.z)) * 1000f:F1} mm", this);
        return passed;
    }

    void BeginGrasp()
    {
        if (automatic && !NearFootprint(localizedPick, ActiveCube.bounds))
        {
            preGraspReached = false;
            status = "Cube moved since localization. Take a new image.";
            return;
        }
        Vector3 cubeTop = CubeTop();
        liftTarget = cubeTop + robotBase.up * clearance;
        Vector3 lateralError = Vector3.ProjectOnPlane(tcp.position - liftTarget, robotBase.up);
        if (lateralError.magnitude > .005f || Mathf.Abs(Vector3.Dot(tcp.position - liftTarget, robotBase.up)) > .01f ||
            Quaternion.Angle(tcp.rotation, targetRotation) > RotationTolerance)
        {
            preGraspReached = false;
            status = "Move above the red cube again.";
            return;
        }
        preGraspReached = false;
        PlanMove(cubeTop - robotBase.up * GraspDepth, Phase.Descend, useTabletopSeed: false);
    }

    void BeginReplay()
    {
        if (phase != Phase.Idle || cubeHeld || !robotBase || !tcp || !finger1 || !finger2 || joints.Count != 7)
            return;

        var asset = Resources.Load<TextAsset>("NeroTransferReplay");
        automatic = false;
        replay = asset ? JsonUtility.FromJson<ReplayTrajectory>(asset.text) : null;
        if (replay?.points == null || replay.points.Length < 2 || !(replay.dt > 0f) || float.IsInfinity(replay.dt))
        {
            status = "Replay file is missing or invalid.";
            return;
        }
        foreach (Vector3 point in replay.points)
        {
            if (float.IsNaN(point.sqrMagnitude) || float.IsInfinity(point.sqrMagnitude))
            {
                status = "Replay contains an invalid point.";
                return;
            }
        }

        preGraspReached = false;
        transferStart = robotBase.TransformPoint(replay.points[0]);
        transferStartRotation = robotBase.rotation * Quaternion.Euler(0f, 0f, 180f);
        targetRotation = transferStartRotation;
        if (!PlanMove(transferStart, Phase.ReplayApproach, useTabletopSeed: true)) return;
        SetTarget(finger1, .05f);
        SetTarget(finger2, -.05f);
    }

    IEnumerator RequestTransfer()
    {
        phase = Phase.AwaitPlan;
        status = "Requesting DemoStitching trajectory...";
        var input = new PlanRequest
        {
            start = robotBase.InverseTransformPoint(tcp.position),
            goal = robotBase.InverseTransformPoint(trayApproach)
        };
        using var request = UnityWebRequest.Post("http://127.0.0.1:8766/plan", JsonUtility.ToJson(input), "application/json");
        pendingPlan = request;
        request.timeout = 30;
        yield return request.SendWebRequest();
        if (pendingPlan != request) yield break;
        pendingPlan = null;
        if (request.result != UnityWebRequest.Result.Success)
        {
            var detail = request.downloadHandler.text;
            StopAutomatic(string.IsNullOrEmpty(detail) ? "Planner unavailable. Start serve_nero_plan.py." : detail);
            yield break;
        }

        try
        {
            replay = JsonUtility.FromJson<ReplayTrajectory>(request.downloadHandler.text);
        }
        catch (System.ArgumentException)
        {
            replay = null;
        }
        if (replay?.points == null || replay.points.Length < 2 || replay.points.Length > 5000 ||
            Mathf.Abs(replay.dt - .01f) > .0001f ||
            Vector3.Distance(robotBase.TransformPoint(replay.points[0]), tcp.position) > .01f ||
            Vector3.Distance(replay.points[replay.points.Length - 1], input.goal) > .011f)
        {
            StopAutomatic("Planner returned an invalid trajectory or the arm moved while waiting.");
            yield break;
        }
        foreach (Vector3 point in replay.points)
        {
            if (float.IsNaN(point.sqrMagnitude) || float.IsInfinity(point.sqrMagnitude))
            {
                StopAutomatic("Planner returned a non-finite point.");
                yield break;
            }
        }
        Debug.Log($"NERO automatic: received {replay.points.Length} DemoStitching points.", this);
        replayMaxError = 0f;
        replayMaxAngle = 0f;
        elapsed = 0f;
        phase = Phase.Replay;
        status = "Following DemoStitching trajectory...";
    }

    bool CubeSlipped() =>
        Vector3.Distance(tcp.InverseTransformPoint(ActiveCube.bounds.center), cubeOffsetAtLiftStart) > .015f;

    void StopAutomatic(string reason)
    {
        if (pendingPlan != null) pendingPlan.Abort();
        pendingPlan = null;
        automatic = false;
        phase = Phase.Idle;
        status = $"Automatic stopped: {reason}";
        Debug.LogWarning($"NERO automatic: {status}", this);
    }

    void StepReplay()
    {
        // Give the articulation time to track the blue tray's approach near the base.
        float playbackStep = replay.dt * (automatic && selectedPlace?.id == "blue_tray" ? 2.5f : 1f);
        float frame = Mathf.Min(elapsed / playbackStep, replay.points.Length - 1);
        int lower = Mathf.Min(Mathf.FloorToInt(frame), replay.points.Length - 2);
        Vector3 reference = robotBase.TransformPoint(Vector3.Lerp(replay.points[lower], replay.points[lower + 1], frame - lower));
        float error = Vector3.Distance(tcp.position, reference);
        replayMaxError = Mathf.Max(replayMaxError, error);
        Vector3 from = Vector3.ProjectOnPlane(transferStart - robotBase.position, robotBase.up);
        Vector3 to = Vector3.ProjectOnPlane(reference - robotBase.position, robotBase.up);
        targetRotation = Quaternion.AngleAxis(Vector3.SignedAngle(from, to, robotBase.up), robotBase.up) * transferStartRotation;
        float angle = Quaternion.Angle(tcp.rotation, targetRotation);
        replayMaxAngle = Mathf.Max(replayMaxAngle, angle);
        bool withinTrackingLimit = error <= .04f && angle <= 12f;
        if (!withinTrackingLimit || !SolveTarget(reference, useTabletopSeed: false))
        {
            if (withinTrackingLimit) DiagnoseIkFailure(reference, $"replay sample {lower}");
            for (int i = 0; i < joints.Count; i++)
                SetTarget(joints[i], joints[i].jointPosition[0] * Mathf.Rad2Deg);
            FinishReplay(false, error, angle, $"stopped at sample {lower}");
            return;
        }
        for (int i = 0; i < joints.Count; i++)
            SetTarget(joints[i], goal[i]);

        if (elapsed >= (replay.points.Length - 1) * playbackStep + .75f)
        {
            // Loaded replay can hand off to an exact tray-approach move before descent.
            float endTolerance = automatic ? .025f : PositionTolerance;
            FinishReplay(error < endTolerance && angle < RotationTolerance &&
                replayMaxError < .025f && replayMaxAngle < RotationTolerance, error, angle, "completed");
        }
    }

    void DiagnoseIkFailure(Vector3 reference, string context)
    {
        Quaternion requestedRotation = targetRotation;
        Quaternion baseRotation = Quaternion.Inverse(robotBase.rotation) * requestedRotation;
        Debug.Log($"NERO IK diagnostic: {context}, base target {robotBase.InverseTransformPoint(reference).ToString("F5")}, " +
            $"base rotation {baseRotation.ToString("F5")}, previous joints [{string.Join(", ", start)}] degrees.", this);

        // Solve only against the captured kinematic chain. Do not command diagnostic solutions.
        var random = new System.Random(587);
        try
        {
            if (TryDiagnosticSeeds(random, 96))
            {
                Debug.Log($"NERO IK diagnostic: same pose solved with alternate joint seed: [{string.Join(", ", goal)}] degrees.", this);
                return;
            }

            foreach (float yaw in new[] { -10f, 10f, -20f, 20f, -35f, 35f })
            {
                targetRotation = Quaternion.AngleAxis(yaw, robotBase.up) * requestedRotation;
                if (!TryDiagnosticSeeds(random, 48)) continue;
                Debug.Log($"NERO IK diagnostic: fixed pose did not solve in the sampled seeds; yaw {yaw:F0} degrees solved: " +
                    $"[{string.Join(", ", goal)}] degrees.", this);
                return;
            }
            Debug.LogWarning("NERO IK diagnostic: no solution found in the tested seeds and yaw offsets.", this);
        }
        finally
        {
            targetRotation = requestedRotation;
        }
    }

    bool TryDiagnosticSeeds(System.Random random, int attempts)
    {
        System.Array.Copy(start, goal, start.Length);
        if (Solve()) return true;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            for (int joint = 0; joint < joints.Count; joint++)
                goal[joint] = Mathf.Lerp(minimum[joint], maximum[joint], (float)random.NextDouble());
            if (Solve()) return true;
        }
        return false;
    }

    void FinishReplay(bool passed, float error, float angle, string detail)
    {
        phase = Phase.Idle;
        status = passed ? "Replay passed" : "Replay failed";
        Debug.Log($"NERO replay: {status}, {detail}, final {error * 1000f:F1} mm / {angle:F1} deg, peak {replayMaxError * 1000f:F1} mm / {replayMaxAngle:F1} deg", this);
        if (automatic)
        {
            if (!passed || Vector3.Distance(tcp.position, trayApproach) > .035f)
                StopAutomatic("Transfer did not reach the tray approach.");
            else
            {
                bool blueTray = selectedPlace?.id == "blue_tray";
                if (blueTray)
                    targetRotation = robotBase.rotation * Quaternion.Euler(0f, 0f, 180f);
                PlanMove(trayReleaseApproach, Phase.AlignForRelease, useTabletopSeed: blueTray);
            }
        }
    }

    bool SolveTarget(Vector3 destination, bool useTabletopSeed)
    {
        target = destination;
        hasTarget = true;
        initialTcp = tcp.position;
        initialRotation = tcp.rotation;
        for (int i = 0; i < joints.Count; i++)
        {
            var joint = joints[i];
            var drive = joint.xDrive;
            pivots[i] = joint.transform.TransformPoint(joint.anchorPosition);
            axes[i] = joint.transform.TransformDirection(joint.anchorRotation * Vector3.right);
            start[i] = joint.jointPosition[0] * Mathf.Rad2Deg;
            minimum[i] = drive.lowerLimit;
            maximum[i] = drive.upperLimit;
            goal[i] = Mathf.Clamp(useTabletopSeed ? TabletopSeed[i] : start[i], minimum[i], maximum[i]);
        }

        if (Solve()) return true;
        if (!useTabletopSeed) return false;
        System.Array.Copy(start, goal, start.Length);
        return Solve();
    }

    bool PlanMove(Vector3 destination, Phase nextPhase, bool useTabletopSeed)
    {
        if (!SolveTarget(destination, useTabletopSeed))
        {
            if (nextPhase == Phase.LowerToTray || nextPhase == Phase.LowerToTrayMidpoint)
                DiagnoseIkFailure(destination, nextPhase.ToString());
            status = $"IK found no solution for {nextPhase}.";
            Debug.LogWarning($"NERO IK: {status}", this);
            return false;
        }
        duration = 2f;
        for (int i = 0; i < joints.Count; i++)
            duration = Mathf.Max(duration, 1.875f * Mathf.Abs(goal[i] - start[i]) / 35f);
        elapsed = 0f;
        phase = nextPhase;
        status = nextPhase switch
        {
            Phase.Approach => "Moving to pre-grasp...",
            Phase.Descend => "Descending...",
            Phase.Lift => "Lifting...",
            Phase.RaiseForTransfer => "Raising for transfer...",
            Phase.TransferMidpoint or Phase.Transfer => "Moving above the white tray...",
            Phase.AlignForRelease => "Aligning above the selected tray...",
            Phase.LowerToTrayMidpoint or Phase.LowerToTray => "Lowering into the selected tray...",
            Phase.Retreat => "Retracting from the tray...",
            Phase.ReplayApproach => "Moving to replay start...",
            _ => status
        };
        return true;
    }

    bool Solve()
    {
        for (int iteration = 0; iteration < 120; iteration++)
        {
            EvaluatePose(out Vector3 position, out Quaternion rotation);
            Vector3 linearError = target - position;
            Quaternion rotationError = targetRotation * Quaternion.Inverse(rotation);
            rotationError.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            if (linearError.sqrMagnitude < .003f * .003f && Mathf.Abs(angle) < 2f) return true;

            Vector3 angularError = axis * (angle * Mathf.Deg2Rad * RotationWeight);
            for (int j = 0; j < joints.Count; j++)
            {
                Vector3 linear = Vector3.Cross(movedAxes[j], position - movedPivots[j]);
                Vector3 angular = movedAxes[j] * RotationWeight;
                jacobian[0, j] = linear.x;
                jacobian[1, j] = linear.y;
                jacobian[2, j] = linear.z;
                jacobian[3, j] = angular.x;
                jacobian[4, j] = angular.y;
                jacobian[5, j] = angular.z;
            }

            // Damped least squares: delta = J^T (J J^T + damping^2 I)^-1 error.
            for (int row = 0; row < 6; row++)
            {
                for (int col = 0; col < 6; col++)
                {
                    float entry = row == col ? .0025f : 0f;
                    for (int j = 0; j < joints.Count; j++) entry += jacobian[row, j] * jacobian[col, j];
                    system[row, col] = entry;
                }
                system[row, 6] = row < 3 ? linearError[row] : angularError[row - 3];
            }
            SolveDampedSystem();

            for (int j = 0; j < joints.Count; j++)
            {
                float delta = 0f;
                for (int row = 0; row < 6; row++) delta += jacobian[row, j] * system[row, 6];
                goal[j] = Mathf.Clamp(goal[j] + Mathf.Clamp(delta * Mathf.Rad2Deg, -8f, 8f), minimum[j], maximum[j]);
            }
        }
        return false;
    }

    void EvaluatePose(out Vector3 position, out Quaternion rotation)
    {
        // Apply each joint delta to the recorded chain, without moving the live articulation.
        Quaternion offsetRotation = Quaternion.identity;
        Vector3 offsetPosition = Vector3.zero;
        for (int i = 0; i < joints.Count; i++)
        {
            Vector3 pivot = offsetPosition + offsetRotation * pivots[i];
            Vector3 axis = offsetRotation * axes[i];
            movedPivots[i] = pivot;
            movedAxes[i] = axis;
            var step = Quaternion.AngleAxis(goal[i] - start[i], axis);
            offsetPosition = pivot + step * (offsetPosition - pivot);
            offsetRotation = step * offsetRotation;
        }
        position = offsetPosition + offsetRotation * initialTcp;
        rotation = offsetRotation * initialRotation;
    }

    void SolveDampedSystem()
    {
        for (int i = 0; i < 6; i++)
        {
            float divisor = system[i, i];
            for (int col = i; col <= 6; col++) system[i, col] /= divisor;
            for (int row = 0; row < 6; row++)
            {
                if (row == i) continue;
                float factor = system[row, i];
                for (int col = i; col <= 6; col++) system[row, col] -= factor * system[i, col];
            }
        }
    }

    void FixedUpdate()
    {
        if (phase == Phase.Idle)
        {
            if (demonstrationRecorder) demonstrationRecorder.CompleteTransferCapture(false);
            demonstrationRecorder = null;
            return;
        }
        elapsed += Time.fixedDeltaTime;
        if (automatic && (phase == Phase.AwaitPlan || phase == Phase.Replay) && CubeSlipped())
        {
            for (int i = 0; i < joints.Count; i++)
                SetTarget(joints[i], joints[i].jointPosition[0] * Mathf.Rad2Deg);
            cubeHeld = false;
            StopAutomatic("Cube slipped.");
            return;
        }
        if (phase == Phase.AwaitPlan) return;
        if (phase == Phase.Replay)
        {
            StepReplay();
            return;
        }
        if (phase == Phase.Release)
        {
            float opening = Mathf.Clamp01(elapsed / ClosingDuration);
            SetTarget(finger1, Mathf.Lerp(GripTarget, .05f, opening));
            SetTarget(finger2, Mathf.Lerp(-GripTarget, -.05f, opening));
            if (elapsed < ClosingDuration + .35f) return;
            phase = Phase.Idle;
            PlanMove(trayReleaseApproach, Phase.Retreat, useTabletopSeed: false);
            return;
        }
        if ((phase == Phase.RaiseForTransfer || phase == Phase.TransferMidpoint || phase == Phase.Transfer ||
             phase == Phase.AlignForRelease || phase == Phase.LowerToTrayMidpoint || phase == Phase.LowerToTray) &&
            CubeSlipped())
        {
            for (int i = 0; i < joints.Count; i++)
                SetTarget(joints[i], joints[i].jointPosition[0] * Mathf.Rad2Deg);
            cubeHeld = false;
            phase = Phase.Idle;
            status = "Place failed: cube slipped during transfer";
            Debug.LogWarning($"NERO place: {status}", this);
            return;
        }
        if (phase == Phase.Close)
        {
            float closing = Mathf.Clamp01(elapsed / ClosingDuration);
            SetTarget(finger1, Mathf.Lerp(finger1Start, GripTarget, closing));
            SetTarget(finger2, Mathf.Lerp(finger2Start, -GripTarget, closing));
            if (elapsed < ClosingDuration + .35f) return;

            cubeOffsetAtLiftStart = tcp.InverseTransformPoint(ActiveCube.bounds.center);
            Debug.Log($"NERO closure: cube offset {cubeOffsetAtLiftStart.ToString("F4")} m, fingers {finger1.jointPosition[0]:F4}/{finger2.jointPosition[0]:F4} m, cube moved {Vector3.Distance(cubeAtClosure, ActiveCube.bounds.center) * 1000f:F1} mm", this);
            phase = Phase.Idle;
            PlanMove(liftTarget, Phase.Lift, useTabletopSeed: false);
            return;
        }

        float t = Mathf.Clamp01(elapsed / duration);
        float blend = t * t * t * (10f + t * (-15f + 6f * t));
        for (int i = 0; i < joints.Count; i++)
            SetTarget(joints[i], Mathf.Lerp(start[i], goal[i], blend));

        if (elapsed < duration + .75f) return;
        Phase completed = phase;
        phase = Phase.Idle;
        float positionError = Vector3.Distance(tcp.position, target);
        float rotationError = Quaternion.Angle(tcp.rotation, targetRotation);
        bool reached = positionError < PositionTolerance && rotationError < RotationTolerance;
        if (completed == Phase.ReplayApproach)
        {
            replayMaxError = 0f;
            replayMaxAngle = 0f;
            if (!reached)
            {
                FinishReplay(false, positionError, rotationError, "start pose not reached");
                return;
            }
            phase = Phase.Replay;
            elapsed = 0f;
            status = "Replaying transfer with open gripper...";
            return;
        }
        if (completed == Phase.Lift)
        {
            float lifted = Vector3.Dot(ActiveCube.bounds.center - cubeAtClosure, robotBase.up);
            float drift = Vector3.Distance(tcp.InverseTransformPoint(ActiveCube.bounds.center), cubeOffsetAtLiftStart);
            cubeHeld = reached && lifted >= .04f && drift <= .015f;
            status = cubeHeld ? $"Lift passed: cube rose {lifted * 1000f:F0} mm" : $"Lift failed: cube rose {lifted * 1000f:F0} mm";
            Debug.Log($"NERO grasp: {status}, cube slip {drift * 1000f:F1} mm, TCP error {positionError * 1000f:F1} mm", this);
            if (automatic && cubeHeld) BeginPlace();
            return;
        }
        if (completed == Phase.RaiseForTransfer || completed == Phase.TransferMidpoint || completed == Phase.Transfer ||
            completed == Phase.AlignForRelease || completed == Phase.LowerToTrayMidpoint || completed == Phase.LowerToTray || completed == Phase.Retreat)
        {
            // The blue tray has a separate alignment phase before descent.
            bool readyForBlueAlignment = completed == Phase.Transfer && selectedPlace?.id == "blue_tray" &&
                positionError < .025f && rotationError < 5f;
            if (!reached && !readyForBlueAlignment)
            {
                status = $"{completed}: stopped outside tolerance";
                Debug.LogWarning($"NERO place: {status}, TCP error {positionError * 1000f:F1} mm / {rotationError:F1} deg", this);
                return;
            }
            switch (completed)
            {
                case Phase.RaiseForTransfer:
                    transferStart = tcp.position;
                    transferStartRotation = targetRotation;
                    if (demonstrationRecorder && !demonstrationRecorder.BeginTransferCapture(demonstrationDataset))
                        demonstrationRecorder = null;
                    if (automatic && useLearnedTransfer) StartCoroutine(RequestTransfer());
                    else
                    {
                        Vector3 midpoint = Vector3.Lerp(transferStart, trayApproach, .5f);
                        if (selectedPlace?.id == "blue_tray")
                            midpoint += robotBase.right * .24f;
                        PlanTransfer(midpoint, Phase.TransferMidpoint);
                    }
                    break;
                case Phase.TransferMidpoint:
                    PlanTransfer(trayApproach, Phase.Transfer);
                    break;
                case Phase.Transfer:
                    if (demonstrationRecorder) demonstrationRecorder.EndTransferCapture();
                    if (selectedPlace?.id == "blue_tray")
                    {
                        targetRotation = robotBase.rotation * Quaternion.Euler(0f, 0f, 180f);
                        PlanMove(trayReleaseApproach, Phase.AlignForRelease, useTabletopSeed: true);
                    }
                    else PlanMove(trayRelease, Phase.LowerToTray, useTabletopSeed: false);
                    break;
                case Phase.AlignForRelease:
                    if (selectedPlace?.id == "blue_tray")
                    {
                        // Keep the preceding IK solution close to the next target
                        // during the loaded descent near the base.
                        trayRelease += robotBase.up * .02f;
                        PlanMove(Vector3.Lerp(trayReleaseApproach, trayRelease, .5f), Phase.LowerToTrayMidpoint, useTabletopSeed: false);
                    }
                    else PlanMove(trayRelease, Phase.LowerToTray, useTabletopSeed: false);
                    break;
                case Phase.LowerToTrayMidpoint:
                    PlanMove(trayRelease, Phase.LowerToTray, useTabletopSeed: false);
                    break;
                case Phase.LowerToTray:
                    cubeHeld = false;
                    phase = Phase.Release;
                    elapsed = 0f;
                    status = "Opening fingers...";
                    break;
                case Phase.Retreat:
                    bool passed = VerifyPlacement();
                    if (demonstrationRecorder) demonstrationRecorder.CompleteTransferCapture(passed);
                    demonstrationRecorder = null;
                    automatic = false;
                    break;
            }
            return;
        }
        if (completed == Phase.Descend && positionError < .004f && rotationError < RotationTolerance)
        {
            Debug.Log($"NERO descent: pad centers {tcp.InverseTransformPoint(pad1.bounds.center).ToString("F4")} / {tcp.InverseTransformPoint(pad2.bounds.center).ToString("F4")}, cube {tcp.InverseTransformPoint(ActiveCube.bounds.center).ToString("F4")}", this);
            phase = Phase.Close;
            elapsed = 0f;
            cubeAtClosure = ActiveCube.bounds.center;
            finger1Start = finger1.xDrive.target;
            finger2Start = finger2.xDrive.target;
            SetGripStiffness(finger1);
            SetGripStiffness(finger2);
            status = "Closing fingers...";
            return;
        }
        preGraspReached = completed == Phase.Approach && reached;
        status = preGraspReached ? "Reached pre-grasp" : "Stopped outside tolerance";
        Debug.Log($"NERO IK: {status}, TCP error {positionError * 1000f:F1} mm / {rotationError:F1} deg", this);
        if (automatic && preGraspReached) BeginGrasp();
    }

    static void SetTarget(ArticulationBody joint, float target)
    {
        var drive = joint.xDrive;
        drive.target = Mathf.Clamp(target, drive.lowerLimit, drive.upperLimit);
        drive.targetVelocity = 0f;
        joint.xDrive = drive;
    }

    static void SetGripStiffness(ArticulationBody finger)
    {
        var drive = finger.xDrive;
        drive.stiffness = 900f;
        drive.forceLimit = 4f;
        finger.xDrive = drive;
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(16, 248, 278, 224), GUI.skin.box);
        GUILayout.Label("NERO pre-grasp IK");
        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && phase == Phase.Idle;
        if (GUILayout.Button("Move above red cube"))
        {
            automatic = false;
            selectedPick = null;
            selectedPlace = null;
            BeginMove();
        }
        GUI.enabled = wasEnabled && phase == Phase.Idle && preGraspReached;
        if (GUILayout.Button("Descend, grip and lift")) BeginGrasp();
        GUI.enabled = wasEnabled && phase == Phase.Idle && cubeHeld;
        if (GUILayout.Button(selectedPlace == null ? "Place in white tray" : "Place in selected tray"))
        {
            automatic = false;
            BeginPlace();
        }
        GUI.enabled = wasEnabled && phase == Phase.Idle && !cubeHeld;
        if (GUILayout.Button("Replay transfer (empty gripper)")) BeginReplay();
        if (GUILayout.Button("Demonstrate blue cube to yellow tray"))
            BeginDemonstration("blue_cube", "Blue Cube", "yellow_tray", "Tray B - Yellow", "nero_unity_blue_yellow");
        if (GUILayout.Button("Demonstrate red cube to blue tray"))
            BeginDemonstration("red_cube", "Red Cube", "blue_tray", "Tray C - Blue", "nero_unity_red_blue");
        GUI.enabled = wasEnabled;
        GUILayout.Label(status);
        if (hasTarget && tcp)
            GUILayout.Label($"TCP error: {Vector3.Distance(tcp.position, target) * 1000f:F1} mm / {Quaternion.Angle(tcp.rotation, targetRotation):F1} deg");
        GUILayout.EndArea();
    }

    void OnDrawGizmos()
    {
        if (!hasTarget) return;
        var previous = Gizmos.color;
        Gizmos.color = Color.magenta;
        Gizmos.DrawWireSphere(target, .013f);
        Gizmos.color = previous;
    }
}
