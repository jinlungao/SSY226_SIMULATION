using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class NeroTrajectoryRecorder : MonoBehaviour
{
    [SerializeField] Transform robotBase;
    [SerializeField] Transform tcp;
    [SerializeField] string datasetPath = "../../DemoStitching/dataset/stitching/nero_unity";
    [SerializeField, Min(0f)] float motionThreshold = .001f;

    readonly List<Vector3> positions = new List<Vector3>();
    readonly List<double> times = new List<double>();
    string groupText = "0";
    string status = "Ready";
    bool recording;
    bool pendingSave;
    bool transferCapture;
    string destinationDataset;
    int group;

    void FixedUpdate()
    {
        if (!recording) return;
        if (!tcp || !robotBase)
        {
            recording = false;
            status = "Recording stopped: TCP or Robot Base is missing.";
            return;
        }

        positions.Add(robotBase.InverseTransformPoint(tcp.position));
        times.Add(Time.fixedTimeAsDouble);
    }

    void StartRecording()
    {
        if (!tcp || !robotBase)
        {
            status = "Assign TCP and Robot Base in the Inspector.";
            return;
        }
        if (!int.TryParse(groupText, out group) || group < 0)
        {
            status = "Enter a nonnegative demonstration number.";
            return;
        }

        positions.Clear();
        times.Clear();
        destinationDataset = datasetPath;
        recording = true;
    }

    public bool BeginTransferCapture(string datasetName)
    {
        if (recording || pendingSave || transferCapture || !tcp || !robotBase) return false;
        positions.Clear();
        times.Clear();
        group = 0;
        destinationDataset = Path.Combine(Path.GetDirectoryName(datasetPath), datasetName);
        transferCapture = true;
        recording = true;
        return true;
    }

    public void EndTransferCapture()
    {
        if (!transferCapture) return;
        recording = false;
        status = "Transfer captured. Waiting for successful placement.";
    }

    public void CompleteTransferCapture(bool placementPassed)
    {
        if (!transferCapture) return;
        transferCapture = false;
        recording = false;
        if (placementPassed) StopRecording();
        else status = "Failed demonstration discarded.";
    }

    void StopRecording()
    {
        recording = false;
        int first = 0;
        int last = positions.Count - 1;
        // Drop idle time before moving and after stopping. Keep pauses within the motion.
        while (first < last && !IsMoving(first, first + 1)) first++;
        while (last > first && !IsMoving(last - 1, last)) last--;

        if (last - first + 1 < 5)
        {
            status = "Too little motion to save. Move the arm while recording.";
            return;
        }

        positions.RemoveRange(last + 1, positions.Count - last - 1);
        times.RemoveRange(last + 1, times.Count - last - 1);
        positions.RemoveRange(0, first);
        times.RemoveRange(0, first);
        pendingSave = true;
        SaveRecording();
    }

    bool IsMoving(int first, int last)
    {
        return Vector3.Distance(positions[first], positions[last]) > motionThreshold * (times[last] - times[first]);
    }

    void SaveRecording()
    {
        string temporaryFile = null;
        try
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, destinationDataset));
            string folder = Path.Combine(root, $"demonstration_{group}");
            Directory.CreateDirectory(folder);

            int index = 0;
            string destination;
            do
            {
                destination = Path.Combine(folder, $"trajectory_{index++}.json");
            } while (File.Exists(destination));

            // The training loader reads every file inside each demonstration directory.
            temporaryFile = Path.Combine(root, $".trajectory-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryFile, BuildJson(), new UTF8Encoding(false));
            File.Move(temporaryFile, destination);
            temporaryFile = null;
            pendingSave = false;
            status = $"Saved {positions.Count} points to demonstration_{group}/{Path.GetFileName(destination)}.";
            Debug.Log($"NERO trajectory saved to: {destination}", this);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            status = "Save failed. Check the destination, then retry.";
            Debug.LogWarning(exception.Message, this);
        }
        finally
        {
            if (temporaryFile != null)
            {
                try { File.Delete(temporaryFile); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    string BuildJson()
    {
        var json = new StringBuilder("{\n  \"x\": [\n");
        for (int i = 0; i < positions.Count; i++)
            AppendVector(json, positions[i], i == positions.Count - 1);

        json.Append("  ],\n  \"x_dot\": [\n");
        for (int i = 0; i < positions.Count; i++)
        {
            int before = Math.Max(0, i - 1);
            int after = Math.Min(positions.Count - 1, i + 1);
            float duration = (float)(times[after] - times[before]);
            Vector3 velocity = (positions[after] - positions[before]) / duration;
            AppendVector(json, velocity, i == positions.Count - 1);
        }
        return json.Append("  ]\n}\n").ToString();
    }

    static void AppendVector(StringBuilder json, Vector3 value, bool isLast)
    {
        json.AppendFormat(CultureInfo.InvariantCulture, "    [{0:R}, {1:R}, {2:R}]", value.x, value.y, value.z);
        if (!isLast) json.Append(',');
        json.AppendLine();
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(16, 108, 278, 131), GUI.skin.box);
        GUILayout.Label("NERO trajectory recorder");
        GUILayout.BeginHorizontal();
        GUILayout.Label("Demonstration number");
        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && !recording && !pendingSave;
        groupText = GUILayout.TextField(groupText, 9, GUILayout.Width(45));
        GUI.enabled = wasEnabled;
        GUILayout.EndHorizontal();

        if (transferCapture)
        {
            GUILayout.Label(recording ? "Capturing transfer..." : status);
        }
        else if (recording)
        {
            if (GUILayout.Button("Stop and Save")) StopRecording();
        }
        else if (pendingSave)
        {
            if (GUILayout.Button("Retry Save")) SaveRecording();
        }
        else if (GUILayout.Button("Start Recording"))
        {
            StartRecording();
        }

        if (!transferCapture) GUILayout.Label(recording ? $"Recording: {positions.Count} samples" : status);
        GUILayout.EndArea();
    }

    void OnDrawGizmosSelected()
    {
        if (!tcp) return;
        Gizmos.DrawWireSphere(tcp.position, .009f);
    }
}
