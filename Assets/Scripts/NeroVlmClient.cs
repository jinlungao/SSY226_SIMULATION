using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class NeroVlmClient : MonoBehaviour
{
    const string Endpoint = "http://127.0.0.1:11434/api/chat";
    const string LocalizationPrompt =
        "Resolve the user's pick-and-place instruction using the image and scene catalog. " +
        "Find the two requested targets in the image and fill both pick and place when they are visible. " +
        "Use the catalog descriptions to resolve colors and relative positions. " +
        "Left and right refer to the input image. For pick, mark the center of the object's visible top. " +
        "For place, mark the center of the destination's interior floor for in, its surface for on, " +
        "or the reference object for next_to. The two points mark different things. " +
        "Coordinates are [x,y] normalized from 0 to 1000, with [0,0] at image top left. " +
        "Use only IDs in the appropriate catalog list. Choose relation in for inside, into, or in a container. " +
        "When both targets match, return status ok. Use needs_clarification only if a target is missing or ambiguous. " +
        "Return JSON only.";

    const string PointSchema =
        @"""point_2d"":{""type"":""array"",""items"":{""type"":""integer"",""minimum"":0,""maximum"":1000},""minItems"":2,""maxItems"":2}";

    static readonly HttpClient Http = new HttpClient(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromMinutes(3) };

    [SerializeField] Camera observationCamera;
    [SerializeField] string model = "qwen3-vl:4b-instruct";
    [SerializeField] bool showPanel = true;
    [SerializeField] SceneTarget[] sceneTargets =
    {
        new("red_cube", "red cube", "Workspace/Movable Blocks/Red Cube", true, "cube"),
        new("blue_cube", "blue cube", "Workspace/Movable Blocks/Blue Cube", true, "cube"),
        new("white_tray", "white tray", "Workspace/Tray A - White/Bottom", false, "tray", .065f),
        new("yellow_tray", "yellow tray", "Workspace/Tray B - Yellow/Bottom", false, "tray"),
        new("blue_tray", "blue tray", "Workspace/Tray C - Blue/Bottom", false, "tray", -.05f)
    };

    CancellationTokenSource pending;
    Texture2D capturedFrame;
    string instruction = "Put the red cube in the white tray.";
    string error;
    bool panelOpen = true;
    Vector2 panelScroll;
    NeroPreGraspIk armController;

    public LocalizationResult LastResult { get; private set; }
    public bool IsBusy => pending != null;

    [Serializable]
    public sealed class LocalizationResult
    {
        public string status;
        public Target pick;
        public Target place;
    }

    [Serializable]
    public sealed class Target
    {
        public string id;
        public int[] point_2d;
        public string relation;
        public BoxCollider SceneCollider { get; internal set; }
        public string ExecutionProfile { get; internal set; }
        public float LandingOffset { get; internal set; }
        public NeroPointProjector.SurfacePoint? Point3D { get; internal set; }
    }

    [Serializable]
    sealed class SceneTarget
    {
        public string id;
        public string description;
        public string scenePath;
        public bool movable;
        public string executionProfile;
        public float landingOffset;

        public SceneTarget(string id, string description, string scenePath, bool movable, string executionProfile,
            float landingOffset = 0f)
        {
            this.id = id;
            this.description = description;
            this.scenePath = scenePath;
            this.movable = movable;
            this.executionProfile = executionProfile;
            this.landingOffset = landingOffset;
        }
    }

    [Serializable]
    sealed class ChatRequest
    {
        public string model;
        public ChatMessage[] messages;
        public bool stream = false;
        public string keep_alive = "5m";
        public ModelOptions options = new ModelOptions();
    }

    [Serializable]
    sealed class ModelOptions
    {
        public int temperature = 0;
        public int num_ctx = 4096;
        public int num_predict = 256;
        public int seed = 42;
    }

    [Serializable]
    sealed class ChatMessage
    {
        public string role;
        public string content;
        public string[] images;
    }

    [Serializable]
    sealed class ChatResponse
    {
        public ChatMessage message;
        public bool done;
    }

    // Call on Unity's main thread. The returned points refer to the frame captured for this request.
    public async Task<LocalizationResult> LocateAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Enter a pick-and-place instruction.");
        if (!isActiveAndEnabled) throw new InvalidOperationException("The VLM client is disabled.");
        if (IsBusy) throw new InvalidOperationException("A VLM request is already in progress.");

        using var cancellation = new CancellationTokenSource();
        pending = cancellation;
        LastResult = null;
        error = null;
        try
        {
            string image = Convert.ToBase64String(CapturePng(out var projectionFrame));
            var visible = sceneTargets
                .Where(entry => entry != null && !string.IsNullOrEmpty(entry.id) &&
                    System.Text.RegularExpressions.Regex.IsMatch(entry.id, @"\A[a-z0-9_]+\z") &&
                    !string.IsNullOrEmpty(entry.scenePath))
                .Select(entry => (entry, collider: GameObject.Find(entry.scenePath)?.GetComponent<BoxCollider>()))
                .Where(item => item.collider && item.collider.enabled)
                .Select(item => (item.entry, item.collider, view: observationCamera.WorldToViewportPoint(item.collider.bounds.center)))
                .Where(item => item.view.z > 0f && item.view.x >= 0f && item.view.x <= 1f && item.view.y >= 0f && item.view.y <= 1f)
                .OrderBy(item => item.view.x)
                .ToArray();
            var picks = visible.Where(item => item.entry.movable).Select(item => item.entry).ToArray();
            var places = visible.Where(item => !item.entry.movable).Select(item => item.entry).ToArray();
            if (picks.Length == 0 || places.Length == 0)
                throw new InvalidOperationException("The scene catalog needs a visible pick object and destination.");
            if (visible.Select(item => item.entry.id).Distinct().Count() != visible.Length)
                throw new InvalidOperationException("Each visible scene target needs a unique ID.");
            string DescribeRegion(SceneTarget entry)
            {
                var view = visible.First(item => item.entry == entry).view;
                string row = view.y > .55f ? "upper" : view.y < .45f ? "lower" : "middle";
                return $"{entry.id} ({entry.description}, {row} part of image)";
            }
            string catalog = "\nMovable objects, left to right: " + string.Join(", ", picks.Select(DescribeRegion)) +
                "\nDestinations, left to right: " + string.Join(", ", places.Select(DescribeRegion));
            var result = await RequestLocalizationAsync(command.Trim(), image, catalog, picks, places, cancellation.Token);
            if (result.status == "ok")
            {
                var pick = visible.First(item => item.entry.id == result.pick.id);
                var place = visible.First(item => item.entry.id == result.place.id);
                result.pick.SceneCollider = pick.collider;
                result.pick.ExecutionProfile = pick.entry.executionProfile;
                result.place.SceneCollider = place.collider;
                result.place.ExecutionProfile = place.entry.executionProfile;
                result.place.LandingOffset = place.entry.landingOffset;
                result.pick.Point3D = projectionFrame?.Project(result.pick.point_2d, isPick: true);
                result.place.Point3D = projectionFrame?.Project(result.place.point_2d, isPick: false);
                Debug.Log($"NERO VLM: {command.Trim()} => {Describe("Pick", result.pick)}; {Describe("Place", result.place)} ({result.place.relation})", this);
            }
            LastResult = result;
            return LastResult;
        }
        finally
        {
            pending = null;
        }
    }

    static string TargetSchema(SceneTarget[] entries, bool destination)
    {
        string ids = string.Join(",", entries.Select(entry => "\"" + entry.id + "\""));
        string relationRequired = destination ? @",""relation""" : "";
        string relationSchema = destination ? @",""relation"":{""enum"":[""in"",""on"",""next_to""]}" : "";
        return @"{""anyOf"":[{""type"":""object"",""additionalProperties"":false," +
            @"""required"":[""id"",""point_2d""" + relationRequired + @"],""properties"":{""id"":{""enum"":[" +
            ids + "]}," + PointSchema + relationSchema + @"}},{""type"":""null""}]}";
    }

    async Task<LocalizationResult> RequestLocalizationAsync(string command, string image, string catalog, SceneTarget[] picks,
        SceneTarget[] places, CancellationToken cancellationToken)
    {
        string schema = @"{""type"":""object"",""additionalProperties"":false,""required"":[""pick"",""place"",""status""]," +
            @"""properties"":{""pick"":" + TargetSchema(picks, false) + @",""place"":" + TargetSchema(places, true) +
            @",""status"":{""enum"":[""ok"",""needs_clarification""]}}}";
        string requestJson = JsonUtility.ToJson(new ChatRequest
        {
            model = model,
            messages = new[]
            {
                new ChatMessage { role = "system", content = LocalizationPrompt + catalog },
                new ChatMessage { role = "user", content = command, images = new[] { image } }
            }
        });
        // JsonUtility serializes fields, so append the JSON Schema as an object rather than a string.
        requestJson = requestJson.Remove(requestJson.Length - 1) + ",\"format\":" + schema + "}";
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(Endpoint, content, cancellationToken);
        string body = await response.Content.ReadAsStringAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama returned HTTP {(int)response.StatusCode}: {body}");

        var chat = JsonUtility.FromJson<ChatResponse>(body);
        if (chat == null || !chat.done || string.IsNullOrWhiteSpace(chat.message?.content))
            throw new FormatException("Ollama returned an incomplete response.");

        var result = JsonUtility.FromJson<LocalizationResult>(chat.message.content);
        if (result == null) throw new FormatException("Ollama returned an invalid target response.");
        if (result.status == "needs_clarification")
            return new LocalizationResult { status = result.status };
        if (result.status != "ok" || !IsValidTarget(result.pick, picks) || !IsValidTarget(result.place, places) ||
            !(result.place.relation is "in" or "on" or "next_to") ||
            result.pick.point_2d.SequenceEqual(result.place.point_2d))
            throw new FormatException("Ollama returned invalid or duplicate pick-and-place targets.");
        return result;
    }

    static bool IsValidTarget(Target target, SceneTarget[] entries)
    {
        return target?.point_2d is { Length: 2 } && entries.Any(entry => entry.id == target.id) &&
            target.point_2d[0] >= 0 && target.point_2d[0] <= 1000 &&
            target.point_2d[1] >= 0 && target.point_2d[1] <= 1000;
    }

    byte[] CapturePng(out NeroPointProjector.Frame? projectionFrame)
    {
        var target = observationCamera ? observationCamera.targetTexture : null;
        if (!target) throw new InvalidOperationException("Assign the observation camera and its target Render Texture.");

        if (capturedFrame) Destroy(capturedFrame);
        capturedFrame = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        try
        {
            observationCamera.Render();
            projectionFrame = TryGetComponent(out NeroPointProjector projector) ?
                projector.CaptureFrame(observationCamera) : null;
            RenderTexture.active = target;
            capturedFrame.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
            capturedFrame.Apply(false);
            return capturedFrame.EncodeToPNG();
        }
        finally
        {
            RenderTexture.active = previous;
        }
    }

    async void SubmitFromPanel(bool execute = false)
    {
        try
        {
            var result = await LocateAsync(instruction);
            if (execute && result.status == "ok" && this && isActiveAndEnabled && armController)
                armController.BeginPickAndPlace(result);
        }
        catch (OperationCanceledException)
        {
            if (this && isActiveAndEnabled) error = "The VLM request timed out or was cancelled.";
        }
        catch (HttpRequestException)
        {
            error = "Cannot reach local Ollama. Run Start-VLM.cmd, then try again.";
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    void OnDisable()
    {
        pending?.Cancel();
        if (capturedFrame) Destroy(capturedFrame);
        capturedFrame = null;
        LastResult = null;
    }

    void OnGUI()
    {
        if (!showPanel) return;
        float scale = Mathf.Max(1, Screen.height / 650f);
        float viewWidth = Screen.width / scale;
        float viewHeight = Screen.height / scale;
        float panelWidth = panelOpen ? Mathf.Clamp(viewWidth * .32f, 220f, 330f) : 148f;
        float width = Mathf.Min(panelWidth, viewWidth - 32);
        if (width < 120 || viewHeight < 80) return;
        const float topMargin = 30f;
        float height = panelOpen ? Mathf.Min(480, viewHeight - topMargin - 16) : 38;
        var previousMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1)) * previousMatrix;
        GUILayout.BeginArea(new Rect(viewWidth - width - 16, topMargin, width, height), GUI.skin.box);
        if (GUILayout.Button(panelOpen ? "NERO VLM  /  Hide" : "NERO VLM  /  Show")) panelOpen = !panelOpen;
        if (panelOpen)
        {
            panelScroll = GUILayout.BeginScrollView(panelScroll);
            GUILayout.Label("Pick-and-place instruction");
            instruction = GUILayout.TextArea(instruction, 400, GUILayout.MinHeight(36));
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && !IsBusy && !string.IsNullOrWhiteSpace(instruction);
            if (GUILayout.Button("Send to VLM")) SubmitFromPanel();
            if (!armController) armController = FindFirstObjectByType<NeroPreGraspIk>();
            GUI.enabled = GUI.enabled && armController && armController.CanBeginAutomatic;
            if (GUILayout.Button("Locate and execute pick-and-place")) SubmitFromPanel(execute: true);
            GUI.enabled = wasEnabled;

            if (IsBusy) GUILayout.Label("Waiting for Ollama…");
            else if (!string.IsNullOrEmpty(error)) GUILayout.Label(error);
            else if (LastResult?.status == "needs_clarification")
                GUILayout.Label("Could not identify both targets. Specify the object and destination.");
            else if (LastResult != null)
            {
                GUILayout.Label(Describe("Pick", LastResult.pick));
                DrawBaseCoordinates(LastResult.pick);
                GUILayout.Label(Describe("Place", LastResult.place));
                DrawBaseCoordinates(LastResult.place);
            }

            GUILayout.EndScrollView();
            if (capturedFrame && height >= 130)
            {
                GUILayout.Label("Sent RGB frame  /  Green: pick  /  Cyan: place");
                DrawFrame(width, height * .53f);
            }
        }
        GUILayout.EndArea();
        GUI.matrix = previousMatrix;
    }

    void DrawFrame(float panelWidth, float maxHeight)
    {
        float aspect = (float)capturedFrame.width / capturedFrame.height;
        float width = Mathf.Min(panelWidth - 24, maxHeight * aspect);
        Rect rect = GUILayoutUtility.GetRect(width, width / aspect, GUILayout.ExpandWidth(false));
        if (Event.current.type != EventType.Repaint) return;
        GUI.DrawTexture(rect, capturedFrame);
        DrawPoint(rect, LastResult?.pick, Color.green);
        DrawPoint(rect, LastResult?.place, Color.cyan);
    }

    static void DrawPoint(Rect rect, Target target, Color color)
    {
        if (!(target?.point_2d is { Length: 2 })) return;
        float x = rect.x + rect.width * target.point_2d[0] / 1000f;
        float y = rect.y + rect.height * target.point_2d[1] / 1000f;
        var previousColor = GUI.color;
        GUI.color = Color.black;
        GUI.DrawTexture(new Rect(x - 6, y - 2, 12, 4), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2, y - 6, 4, 12), Texture2D.whiteTexture);
        GUI.color = color;
        GUI.DrawTexture(new Rect(x - 5, y - 1, 10, 2), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 1, y - 5, 2, 10), Texture2D.whiteTexture);
        GUI.color = previousColor;
    }

    static string Describe(string role, Target target)
    {
        return !(target?.point_2d is { Length: 2 }) ? $"{role}: no visible match" :
            $"{role}: {target.id} [{target.point_2d[0]}, {target.point_2d[1]}]";
    }

    static void DrawBaseCoordinates(Target target)
    {
        if (target == null) return;
        GUILayout.Label(target.Point3D is { } point ?
            $"  base_link {point.BasePosition.ToString("F3")} m" : "  3D point unavailable");
    }
}
