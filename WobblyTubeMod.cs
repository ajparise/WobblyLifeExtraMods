using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WobblyLifeExtraMods;

/// <summary>An in-game channel, screen recorder, and simple cut/timelapse editor.</summary>
public sealed class WobblyTubeMod : BaseMod
{
    private const float MaximumRecordingSeconds = 300f;
    private const int WindowId = 0x574254;
    private static readonly Ref<string> Status = new("Open WobblyTube Studio to create your channel.");
    private static readonly ConcurrentQueue<FramePacket> FrameQueue = new();
    private static readonly AutoResetEvent FrameReady = new(false);
    private static readonly List<VideoProject> Projects = new();
    private static readonly WaitForEndOfFrame EndOfFrame = new();
    private static Rect studioRect;
    private static Vector2 studioScroll;
    private static bool initialized;
    private static bool studioVisible;
    private static bool cursorStateSaved;
    private static bool previousCursorVisible;
    private static CursorLockMode previousCursorLock;
    private static string rootDirectory;
    private static string projectsDirectory;
    private static string videosDirectory;
    private static string channelName = "";
    private static string channelDraft = "My Wobbly Channel";
    private static string videoNameDraft = "My Wobbly Video";
    private static int selectedProject = -1;
    private static string editStartText = "0";
    private static string editEndText = "5";
    private static bool timelapseEdit;
    private static string confirmDeleteDirectory;
    private static string currentProjectDirectory;
    private static string currentVideoTitle;
    private static int currentFps;
    private static int currentWidth;
    private static int currentHeight;
    private static int currentJpegQuality;
    private static int captureSequence;
    private static int queuedFrames;
    private static float recordingStartedAt;
    private static float nextCaptureAt;
    private static Thread writerThread;
    private static Thread exportThread;
    private static volatile bool writerRunning;
    private static volatile bool exporting;
    private static volatile string writerError;
    private static volatile string threadMessage;
    private static bool finalizing;

    public override string Name => "WobblyTube";
    public override string Description =>
        "Create a channel, record up to five minutes, name videos, and edit a range into a cut or timelapse.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 5f, Max = 20f, Label = "Recording frames per second")]
    public static Ref<int> CaptureFramesPerSecond = new(10);
    [ModSetting(Order = 20, Min = 480f, Max = 1280f, Label = "Recording width")]
    public static Ref<int> CaptureWidth = new(854);
    [ModSetting(Order = 30, Min = 35f, Max = 90f, Label = "Video picture quality")]
    public static Ref<int> PictureQuality = new(68);
    [ModSetting(Order = 40, Min = 2f, Max = 10f, Label = "Timelapse speed")]
    public static Ref<int> TimelapseSpeed = new(4);

    public override Container BuildPanel(string id)
    {
        EnsureInitialized();
        return new Container(id,
            new TextWrapped("WobblyTubeHelp",
                "Open WobblyTube Studio, create and name a channel, then name a video and press the red record button. " +
                "Recordings stop automatically at five minutes. F7 opens the studio and F8 starts or stops recording. " +
                "Select a recorded project to remove a chosen time range or turn that range into a timelapse, then export " +
                "it as an AVI video. Originals stay editable. The built-in recorder captures video pictures without game audio."),
            base.BuildPanel(id),
            new HStack("WobblyTubeActions",
                ActionMenu(new Button("Open WobblyTube Studio", OpenStudio), nameof(OpenStudio)),
                ActionMenu(new Button("Start / stop recording", ToggleRecording), nameof(ToggleRecording)),
                ActionMenu(new Button("Open video folder", OpenVideosFolder), nameof(OpenVideosFolder))
            ).WithContentWidth(),
            new TextWrapped("WobblyTubeStatus", "").WithText(Status));
    }

    public override void Update()
    {
        EnsureInitialized();
        ConsumeThreadMessage();
        if (Input.GetKeyDown(KeyCode.F7))
        {
            if (studioVisible) HideStudio();
            else OpenStudio();
        }
        if (Input.GetKeyDown(KeyCode.F8)) ToggleRecording();
        if (studioVisible)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
        if (IsRecording && Time.realtimeSinceStartup - recordingStartedAt >= MaximumRecordingSeconds)
        {
            Status.Value = "Five-minute recording limit reached. Finishing the video project...";
            StopRecording();
        }
    }

    [ModAction(ShowInUI = false)]
    public static void OpenStudio()
    {
        EnsureInitialized();
        if (studioVisible) return;
        studioVisible = true;
        if (studioRect.width < 1f)
            studioRect = new Rect(Mathf.Max(10f, (Screen.width - 760f) * 0.5f),
                Mathf.Max(10f, (Screen.height - 650f) * 0.5f), 760f, 650f);
        previousCursorVisible = Cursor.visible;
        previousCursorLock = Cursor.lockState;
        cursorStateSaved = true;
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        Status.Value = string.IsNullOrWhiteSpace(channelName)
            ? "Create your WobblyTube channel to begin."
            : $"WobblyTube Studio opened for {channelName}.";
    }

    [ModAction(ShowInUI = false)]
    public static void ToggleRecording()
    {
        EnsureInitialized();
        if (IsRecording) StopRecording();
        else StartRecording();
    }

    [ModAction(ShowInUI = false)]
    public static void OpenVideosFolder()
    {
        EnsureInitialized();
        Directory.CreateDirectory(videosDirectory);
        Application.OpenURL(new Uri(videosDirectory).AbsoluteUri);
        Status.Value = $"Opened {videosDirectory}";
    }

    internal static void DrawStudio()
    {
        EnsureInitialized();
        ConsumeThreadMessage();
        if (IsRecording) DrawRecordingHud();
        if (!studioVisible) return;
        studioRect.x = Mathf.Clamp(studioRect.x, 0f, Mathf.Max(0f, Screen.width - 120f));
        studioRect.y = Mathf.Clamp(studioRect.y, 0f, Mathf.Max(0f, Screen.height - 40f));
        studioRect = GUI.Window(WindowId, studioRect, DrawStudioWindow, "WobblyTube Studio");
    }

    internal static void Shutdown()
    {
        IsRecording = false;
        writerRunning = false;
        FrameReady.Set();
        studioVisible = false;
    }

    private static bool IsRecording { get; set; }

    private static void DrawRecordingHud()
    {
        var elapsed = Mathf.Clamp(Time.realtimeSinceStartup - recordingStartedAt, 0f, MaximumRecordingSeconds);
        var rect = new Rect(Screen.width - 245f, 18f, 225f, 58f);
        var oldColor = GUI.color;
        GUI.color = new Color(0.12f, 0.02f, 0.02f, 0.9f);
        GUI.Box(rect, GUIContent.none);
        GUI.color = Color.white;
        GUI.Label(new Rect(rect.x + 12f, rect.y + 7f, 200f, 24f),
            $"● REC  {FormatTime(elapsed)} / 05:00");
        GUI.Label(new Rect(rect.x + 12f, rect.y + 30f, 200f, 20f), "F8 to stop recording");
        GUI.color = oldColor;
    }

    private static void DrawStudioWindow(int id)
    {
        GUILayout.BeginVertical();
        GUILayout.Space(4f);
        if (string.IsNullOrWhiteSpace(channelName)) DrawCreateChannel();
        else DrawChannelStudio();
        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal();
        GUILayout.Label(Status.Value ?? "", GUILayout.ExpandWidth(true));
        if (GUILayout.Button("Hide Studio", GUILayout.Width(110f))) HideStudio();
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
        GUI.DragWindow(new Rect(0f, 0f, studioRect.width - 130f, 25f));
    }

    private static void DrawCreateChannel()
    {
        GUILayout.Space(45f);
        var titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 28,
            fontStyle = FontStyle.Bold
        };
        GUILayout.Label("CREATE WOBBLYTUBE CHANNEL", titleStyle, GUILayout.Height(52f));
        GUILayout.Label("Choose a channel name. You can rename it later.", CenteredLabel());
        GUILayout.Space(15f);
        channelDraft = GUILayout.TextField(channelDraft ?? "", 32, GUILayout.Height(34f));
        GUILayout.Space(10f);
        var oldBackground = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.95f, 0.08f, 0.08f);
        if (GUILayout.Button("CREATE CHANNEL", GUILayout.Height(48f))) CreateChannel();
        GUI.backgroundColor = oldBackground;
    }

    private static void DrawChannelStudio()
    {
        studioScroll = GUILayout.BeginScrollView(studioScroll);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Channel", GUILayout.Width(62f));
        channelDraft = GUILayout.TextField(channelDraft ?? "", 32, GUILayout.Height(28f));
        if (GUILayout.Button("Save channel name", GUILayout.Width(145f), GUILayout.Height(28f))) SaveChannelName();
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);
        GUILayout.Label("NEW VIDEO NAME");
        videoNameDraft = GUILayout.TextField(videoNameDraft ?? "", 64, GUILayout.Height(30f));
        GUILayout.Space(7f);

        var oldBackground = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.95f, 0.04f, 0.04f);
        if (IsRecording)
        {
            var elapsed = Time.realtimeSinceStartup - recordingStartedAt;
            if (GUILayout.Button($"■  STOP RECORDING  {FormatTime(elapsed)}", GUILayout.Height(48f))) StopRecording();
        }
        else if (finalizing)
        {
            GUI.enabled = false;
            GUILayout.Button("FINISHING RECORDING...", GUILayout.Height(48f));
            GUI.enabled = true;
        }
        else if (GUILayout.Button("●  RECORD", GUILayout.Height(48f))) StartRecording();
        GUI.backgroundColor = oldBackground;

        GUILayout.BeginHorizontal();
        GUILayout.Label($"Maximum 05:00  •  {Mathf.Clamp(CaptureFramesPerSecond.Value, 5, 20)} FPS  •  " +
                        $"{Mathf.Clamp(CaptureWidth.Value, 480, 1280)} px wide");
        if (GUILayout.Button("Open videos folder", GUILayout.Width(145f))) OpenVideosFolder();
        GUILayout.EndHorizontal();
        GUILayout.Space(12f);
        GUILayout.Label("RECORDED PROJECTS", SectionLabel());
        if (Projects.Count == 0)
        {
            GUILayout.Label("No recordings yet. Name a video and press the red RECORD button.");
        }
        else
        {
            for (var i = 0; i < Projects.Count; i++)
            {
                var project = Projects[i];
                var selected = selectedProject == i;
                var previous = GUI.backgroundColor;
                if (selected) GUI.backgroundColor = new Color(1f, 0.25f, 0.25f);
                if (GUILayout.Button($"{project.Title}   •   {FormatTime(project.Duration)}   •   {project.FrameCount} frames"))
                {
                    selectedProject = i;
                    videoNameDraft = project.Title;
                    editStartText = "0";
                    editEndText = project.Duration.ToString("0.0", CultureInfo.InvariantCulture);
                    confirmDeleteDirectory = null;
                }
                GUI.backgroundColor = previous;
            }
        }
        if (selectedProject >= 0 && selectedProject < Projects.Count) DrawEditor(Projects[selectedProject]);
        GUILayout.EndScrollView();
    }

    private static void DrawEditor(VideoProject project)
    {
        GUILayout.Space(14f);
        GUILayout.Label("VIDEO EDITOR", SectionLabel());
        GUILayout.BeginHorizontal();
        GUILayout.Label("Video name", GUILayout.Width(78f));
        videoNameDraft = GUILayout.TextField(videoNameDraft ?? "", 64);
        if (GUILayout.Button("Save name", GUILayout.Width(95f))) RenameProject(project);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("Range start", GUILayout.Width(78f));
        editStartText = GUILayout.TextField(editStartText ?? "0", GUILayout.Width(80f));
        GUILayout.Label("seconds", GUILayout.Width(58f));
        GUILayout.Label("Range end", GUILayout.Width(72f));
        editEndText = GUILayout.TextField(editEndText ?? "0", GUILayout.Width(80f));
        GUILayout.Label($"seconds (video ends at {project.Duration:0.0})");
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        timelapseEdit = GUILayout.Toggle(!timelapseEdit, "Cut: remove this range", GUI.skin.button,
            GUILayout.Height(34f)) ? false : timelapseEdit;
        timelapseEdit = GUILayout.Toggle(timelapseEdit,
            $"Timelapse: make this range {Mathf.Clamp(TimelapseSpeed.Value, 2, 10)}× faster", GUI.skin.button,
            GUILayout.Height(34f));
        GUILayout.EndHorizontal();
        GUI.enabled = !exporting && !IsRecording && !finalizing;
        if (GUILayout.Button(exporting ? "EXPORTING VIDEO..." : "EXPORT EDITED VIDEO", GUILayout.Height(40f)))
            ExportProject(project);
        GUILayout.BeginHorizontal();
        var confirm = string.Equals(confirmDeleteDirectory, project.Directory, StringComparison.OrdinalIgnoreCase);
        if (GUILayout.Button(confirm ? "Click again to delete project" : "Delete raw project", GUILayout.Width(190f)))
        {
            if (confirm) DeleteProject(project);
            else confirmDeleteDirectory = project.Directory;
        }
        GUILayout.Label("Exported AVI files are kept when raw projects are deleted.");
        GUILayout.EndHorizontal();
        GUI.enabled = true;
    }

    private static GUIStyle CenteredLabel()
    {
        return new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
    }

    private static GUIStyle SectionLabel()
    {
        return new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 16 };
    }

    private static void CreateChannel()
    {
        channelDraft = CleanDisplayName(channelDraft, "My Wobbly Channel", 32);
        channelName = channelDraft;
        WriteEncodedText(Path.Combine(rootDirectory, "channel.txt"), channelName);
        Status.Value = $"Created WobblyTube channel: {channelName}";
    }

    private static void SaveChannelName()
    {
        channelDraft = CleanDisplayName(channelDraft, channelName, 32);
        channelName = channelDraft;
        WriteEncodedText(Path.Combine(rootDirectory, "channel.txt"), channelName);
        Status.Value = $"Channel renamed to {channelName}.";
    }

    private static void StartRecording()
    {
        EnsureInitialized();
        if (IsRecording || finalizing || exporting) return;
        if (string.IsNullOrWhiteSpace(channelName))
        {
            Status.Value = "Open the studio and create a WobblyTube channel first.";
            OpenStudio();
            return;
        }
        currentVideoTitle = CleanDisplayName(videoNameDraft, $"Wobbly Video {DateTime.Now:HH-mm-ss}", 64);
        videoNameDraft = currentVideoTitle;
        currentFps = Mathf.Clamp(CaptureFramesPerSecond.Value, 5, 20);
        currentWidth = MakeEven(Mathf.Clamp(CaptureWidth.Value, 480, 1280));
        var aspect = Screen.width > 0 && Screen.height > 0 ? (float)Screen.height / Screen.width : 9f / 16f;
        currentHeight = MakeEven(Mathf.Max(270, Mathf.RoundToInt(currentWidth * aspect)));
        currentJpegQuality = Mathf.Clamp(PictureQuality.Value, 35, 90);
        currentProjectDirectory = Path.Combine(projectsDirectory, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(currentProjectDirectory);
        while (FrameQueue.TryDequeue(out _)) { }
        queuedFrames = 0;
        captureSequence = 0;
        writerError = null;
        writerRunning = true;
        writerThread = new Thread(WriteFrames) { IsBackground = true, Name = "WobblyTube frame writer" };
        writerThread.Start();
        recordingStartedAt = Time.realtimeSinceStartup;
        nextCaptureAt = recordingStartedAt;
        IsRecording = true;
        Plugin.RunCoroutine(CaptureFrames());
        Status.Value = $"Recording {currentVideoTitle}. Press F8 to stop; maximum length is five minutes.";
        HideStudio();
    }

    private static void StopRecording()
    {
        if (!IsRecording) return;
        IsRecording = false;
        finalizing = true;
        Status.Value = "Recording stopped. Finishing the WobblyTube project...";
        Plugin.RunCoroutine(FinishRecording());
    }

    private static IEnumerator CaptureFrames()
    {
        while (IsRecording)
        {
            yield return EndOfFrame;
            if (!IsRecording) break;
            var now = Time.realtimeSinceStartup;
            if (now < nextCaptureAt) continue;
            nextCaptureAt += 1f / currentFps;
            if (now - nextCaptureAt > 1f) nextCaptureAt = now;
            if (Volatile.Read(ref queuedFrames) > currentFps * 3) continue;
            CaptureFrame();
        }
        writerRunning = false;
        FrameReady.Set();
    }

    private static void CaptureFrame()
    {
        Texture2D source = null;
        Texture2D scaled = null;
        RenderTexture target = null;
        var previousTarget = RenderTexture.active;
        try
        {
            source = ScreenCapture.CaptureScreenshotAsTexture();
            if (!source) return;
            target = RenderTexture.GetTemporary(currentWidth, currentHeight, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, target);
            RenderTexture.active = target;
            scaled = new Texture2D(currentWidth, currentHeight, TextureFormat.RGB24, false);
            scaled.ReadPixels(new Rect(0f, 0f, currentWidth, currentHeight), 0, 0, false);
            scaled.Apply(false, false);
            var bytes = scaled.EncodeToJPG(currentJpegQuality);
            var number = Interlocked.Increment(ref captureSequence);
            var path = Path.Combine(currentProjectDirectory, $"frame_{number:000000}.jpg");
            FrameQueue.Enqueue(new FramePacket(path, bytes));
            Interlocked.Increment(ref queuedFrames);
            FrameReady.Set();
        }
        catch (Exception exception)
        {
            writerError = exception.Message;
            Status.Value = $"Recording failed: {exception.Message}";
            StopRecording();
        }
        finally
        {
            RenderTexture.active = previousTarget;
            if (target) RenderTexture.ReleaseTemporary(target);
            if (source) Object.Destroy(source);
            if (scaled) Object.Destroy(scaled);
        }
    }

    private static void WriteFrames()
    {
        while (writerRunning || !FrameQueue.IsEmpty)
        {
            if (!FrameQueue.TryDequeue(out var packet))
            {
                FrameReady.WaitOne(200);
                continue;
            }
            try { File.WriteAllBytes(packet.Path, packet.Bytes); }
            catch (Exception exception) { writerError = exception.Message; }
            finally { Interlocked.Decrement(ref queuedFrames); }
        }
    }

    private static IEnumerator FinishRecording()
    {
        while (writerRunning || (writerThread != null && writerThread.IsAlive)) yield return null;
        var frames = Directory.Exists(currentProjectDirectory)
            ? Directory.GetFiles(currentProjectDirectory, "frame_*.jpg").OrderBy(path => path).ToArray()
            : Array.Empty<string>();
        if (frames.Length == 0)
        {
            Status.Value = string.IsNullOrWhiteSpace(writerError)
                ? "No frames were captured. Try a lower recording width."
                : $"Recording failed: {writerError}";
        }
        else
        {
            var project = new VideoProject(currentProjectDirectory, currentVideoTitle, currentFps, currentWidth,
                currentHeight, frames.Length);
            SaveProject(project);
            ReloadProjects();
            selectedProject = Projects.FindIndex(item => item.Directory == currentProjectDirectory);
            editStartText = "0";
            editEndText = project.Duration.ToString("0.0", CultureInfo.InvariantCulture);
            Status.Value = string.IsNullOrWhiteSpace(writerError)
                ? $"Saved {project.Title} ({FormatTime(project.Duration)}). Open Studio to edit or export it."
                : $"Saved {project.Title}, but some frames failed: {writerError}";
        }
        currentProjectDirectory = null;
        writerThread = null;
        finalizing = false;
    }

    private static void RenameProject(VideoProject project)
    {
        project.Title = CleanDisplayName(videoNameDraft, project.Title, 64);
        videoNameDraft = project.Title;
        SaveProject(project);
        Status.Value = $"Video renamed to {project.Title}.";
    }

    private static void ExportProject(VideoProject project)
    {
        if (exporting) return;
        if (!TryParseTime(editStartText, out var start) || !TryParseTime(editEndText, out var end))
        {
            Status.Value = "Enter valid start and end times, such as 2.5 and 8.";
            return;
        }
        start = Mathf.Clamp(start, 0f, project.Duration);
        end = Mathf.Clamp(end, 0f, project.Duration);
        if (end <= start)
        {
            Status.Value = "The range end must be after the range start.";
            return;
        }
        RenameProject(project);
        var title = project.Title;
        var modeIsTimelapse = timelapseEdit;
        var factor = Mathf.Clamp(TimelapseSpeed.Value, 2, 10);
        exporting = true;
        Status.Value = modeIsTimelapse
            ? $"Exporting {title} with a {factor}× timelapse range..."
            : $"Exporting {title} with the selected range removed...";
        exportThread = new Thread(() => ExportWorker(project, start, end, modeIsTimelapse, factor))
        {
            IsBackground = true,
            Name = "WobblyTube AVI exporter"
        };
        exportThread.Start();
    }

    private static void ExportWorker(VideoProject project, float start, float end, bool makeTimelapse, int factor)
    {
        try
        {
            var sourceFrames = Directory.GetFiles(project.Directory, "frame_*.jpg").OrderBy(path => path).ToArray();
            var selectedFrames = new List<string>(sourceFrames.Length);
            var insideIndex = 0;
            for (var i = 0; i < sourceFrames.Length; i++)
            {
                var time = (float)i / project.Fps;
                var inside = time >= start && time < end;
                if (!inside) selectedFrames.Add(sourceFrames[i]);
                else if (makeTimelapse && insideIndex++ % factor == 0) selectedFrames.Add(sourceFrames[i]);
            }
            if (selectedFrames.Count == 0) throw new InvalidOperationException("The edit removed every frame.");
            Directory.CreateDirectory(videosDirectory);
            var suffix = makeTimelapse ? $"timelapse-{factor}x" : "cut";
            var output = Path.Combine(videosDirectory,
                $"{SafeFileName(project.Title)}_{suffix}_{DateTime.Now:yyyyMMdd_HHmmss}.avi");
            MjpegAviWriter.Write(output, selectedFrames, project.Width, project.Height, project.Fps);
            threadMessage = $"Export finished: {Path.GetFileName(output)}";
        }
        catch (Exception exception)
        {
            threadMessage = $"Video export failed: {exception.Message}";
        }
        finally { exporting = false; }
    }

    private static void DeleteProject(VideoProject project)
    {
        try
        {
            Directory.Delete(project.Directory, true);
            Status.Value = $"Deleted raw project {project.Title}. Exported videos were not removed.";
            confirmDeleteDirectory = null;
            ReloadProjects();
            selectedProject = -1;
        }
        catch (Exception exception) { Status.Value = $"Could not delete project: {exception.Message}"; }
    }

    private static void HideStudio()
    {
        studioVisible = false;
        if (!cursorStateSaved) return;
        Cursor.visible = previousCursorVisible;
        Cursor.lockState = previousCursorLock;
        cursorStateSaved = false;
    }

    private static void EnsureInitialized()
    {
        if (initialized) return;
        rootDirectory = Path.Combine(Application.persistentDataPath, "WobblyTube");
        projectsDirectory = Path.Combine(rootDirectory, "Projects");
        videosDirectory = Path.Combine(rootDirectory, "Videos");
        Directory.CreateDirectory(projectsDirectory);
        Directory.CreateDirectory(videosDirectory);
        var channelPath = Path.Combine(rootDirectory, "channel.txt");
        if (File.Exists(channelPath)) channelName = ReadEncodedText(channelPath);
        channelDraft = string.IsNullOrWhiteSpace(channelName) ? "My Wobbly Channel" : channelName;
        ReloadProjects();
        initialized = true;
    }

    private static void ReloadProjects()
    {
        Projects.Clear();
        if (!Directory.Exists(projectsDirectory)) return;
        foreach (var directory in Directory.GetDirectories(projectsDirectory)
                     .OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            var project = LoadProject(directory);
            if (project != null && project.FrameCount > 0) Projects.Add(project);
        }
    }

    private static VideoProject LoadProject(string directory)
    {
        try
        {
            var manifest = Path.Combine(directory, "project.txt");
            if (!File.Exists(manifest)) return null;
            var lines = File.ReadAllLines(manifest);
            if (lines.Length < 5) return null;
            var title = Encoding.UTF8.GetString(Convert.FromBase64String(lines[0]));
            var fps = int.Parse(lines[1], CultureInfo.InvariantCulture);
            var width = int.Parse(lines[2], CultureInfo.InvariantCulture);
            var height = int.Parse(lines[3], CultureInfo.InvariantCulture);
            var frames = Directory.GetFiles(directory, "frame_*.jpg").Length;
            return new VideoProject(directory, title, fps, width, height, frames);
        }
        catch (Exception exception)
        {
            Plugin.Log?.LogWarning($"Could not load WobblyTube project {directory}: {exception.Message}");
            return null;
        }
    }

    private static void SaveProject(VideoProject project)
    {
        var lines = new[]
        {
            Convert.ToBase64String(Encoding.UTF8.GetBytes(project.Title)),
            project.Fps.ToString(CultureInfo.InvariantCulture),
            project.Width.ToString(CultureInfo.InvariantCulture),
            project.Height.ToString(CultureInfo.InvariantCulture),
            project.FrameCount.ToString(CultureInfo.InvariantCulture)
        };
        File.WriteAllLines(Path.Combine(project.Directory, "project.txt"), lines);
    }

    private static void ConsumeThreadMessage()
    {
        if (string.IsNullOrWhiteSpace(threadMessage)) return;
        Status.Value = threadMessage;
        threadMessage = null;
    }

    private static bool TryParseTime(string value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
               float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result);
    }

    private static string CleanDisplayName(string value, string fallback, int maximumLength)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value)) value = fallback;
        return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();
        foreach (var character in CleanDisplayName(value, "Wobbly Video", 64))
            builder.Append(invalid.Contains(character) ? '_' : character);
        return builder.ToString().Trim().TrimEnd('.');
    }

    private static void WriteEncodedText(string path, string value)
    {
        File.WriteAllText(path, Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
    }

    private static string ReadEncodedText(string path)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(path))); }
        catch { return ""; }
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;

    private static string FormatTime(float seconds)
    {
        seconds = Mathf.Max(0f, seconds);
        return $"{Mathf.FloorToInt(seconds / 60f):00}:{Mathf.FloorToInt(seconds % 60f):00}";
    }

    private sealed class FramePacket
    {
        internal readonly string Path;
        internal readonly byte[] Bytes;
        internal FramePacket(string path, byte[] bytes) { Path = path; Bytes = bytes; }
    }

    private sealed class VideoProject
    {
        internal readonly string Directory;
        internal string Title;
        internal readonly int Fps;
        internal readonly int Width;
        internal readonly int Height;
        internal readonly int FrameCount;
        internal float Duration => Fps > 0 ? (float)FrameCount / Fps : 0f;
        internal VideoProject(string directory, string title, int fps, int width, int height, int frameCount)
        {
            Directory = directory;
            Title = title;
            Fps = fps;
            Width = width;
            Height = height;
            FrameCount = frameCount;
        }
    }

    private static class MjpegAviWriter
    {
        internal static void Write(string path, IList<string> frames, int width, int height, int fps)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new BinaryWriter(stream);
            FourCc(writer, "RIFF");
            var riffSize = stream.Position;
            writer.Write(0);
            FourCc(writer, "AVI ");
            FourCc(writer, "LIST");
            var headerSize = stream.Position;
            writer.Write(0);
            FourCc(writer, "hdrl");
            WriteMainHeader(writer, width, height, fps, frames.Count);
            WriteStreamHeader(writer, width, height, fps, frames.Count);
            PatchSize(writer, headerSize, stream.Position - headerSize - 4);
            FourCc(writer, "LIST");
            var movieSize = stream.Position;
            writer.Write(0);
            var movieData = stream.Position;
            FourCc(writer, "movi");
            var index = new List<AviIndex>(frames.Count);
            foreach (var framePath in frames)
            {
                var bytes = File.ReadAllBytes(framePath);
                var chunkPosition = stream.Position;
                FourCc(writer, "00dc");
                writer.Write(bytes.Length);
                writer.Write(bytes);
                if ((bytes.Length & 1) != 0) writer.Write((byte)0);
                index.Add(new AviIndex((int)(chunkPosition - movieData), bytes.Length));
            }
            PatchSize(writer, movieSize, stream.Position - movieSize - 4);
            FourCc(writer, "idx1");
            writer.Write(index.Count * 16);
            foreach (var item in index)
            {
                FourCc(writer, "00dc");
                writer.Write(0x10);
                writer.Write(item.Offset);
                writer.Write(item.Size);
            }
            PatchSize(writer, riffSize, stream.Length - 8);
        }

        private static void WriteMainHeader(BinaryWriter writer, int width, int height, int fps, int frameCount)
        {
            FourCc(writer, "avih");
            writer.Write(56);
            writer.Write(1000000 / fps);
            writer.Write(width * height * 3 * fps);
            writer.Write(0);
            writer.Write(0x10);
            writer.Write(frameCount);
            writer.Write(0);
            writer.Write(1);
            writer.Write(width * height * 3);
            writer.Write(width);
            writer.Write(height);
            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        }

        private static void WriteStreamHeader(BinaryWriter writer, int width, int height, int fps, int frameCount)
        {
            FourCc(writer, "LIST");
            writer.Write(116);
            FourCc(writer, "strl");
            FourCc(writer, "strh");
            writer.Write(56);
            FourCc(writer, "vids");
            FourCc(writer, "MJPG");
            writer.Write(0);
            writer.Write((short)0); writer.Write((short)0);
            writer.Write(0);
            writer.Write(1);
            writer.Write(fps);
            writer.Write(0);
            writer.Write(frameCount);
            writer.Write(width * height * 3);
            writer.Write(-1);
            writer.Write(0);
            writer.Write((short)0); writer.Write((short)0);
            writer.Write((short)width); writer.Write((short)height);
            FourCc(writer, "strf");
            writer.Write(40);
            writer.Write(40);
            writer.Write(width);
            writer.Write(height);
            writer.Write((short)1);
            writer.Write((short)24);
            FourCc(writer, "MJPG");
            writer.Write(width * height * 3);
            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        }

        private static void PatchSize(BinaryWriter writer, long position, long size)
        {
            var end = writer.BaseStream.Position;
            writer.BaseStream.Position = position;
            writer.Write(checked((int)size));
            writer.BaseStream.Position = end;
        }

        private static void FourCc(BinaryWriter writer, string value)
        {
            writer.Write(Encoding.ASCII.GetBytes(value));
        }

        private readonly struct AviIndex
        {
            internal readonly int Offset;
            internal readonly int Size;
            internal AviIndex(int offset, int size) { Offset = offset; Size = size; }
        }
    }
}
