using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimpleJSON;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace BsDownmap.ChroMapper
{
    public abstract class DownmapWindow : MonoBehaviour
    {
        protected const float ControlHeight = 26;
        private Type[] disabled;
        private GUISkin skin;
        private GUIStyle panel, heading;
        private bool laidOut;
        protected abstract string Title { get; }
        protected abstract float Height { get; }
        protected virtual float Width => 960;
        protected abstract void Draw();

        protected virtual void Awake()
        {
            disabled = typeof(CMInput).GetNestedTypes().Where(t => t.IsInterface).ToArray();
            CMInputCallbackInstaller.DisableActionMaps(GetType(), disabled);
        }

        protected virtual void OnDestroy()
        {
            CMInputCallbackInstaller.ClearDisabledActionMaps(GetType(), disabled);
            if (skin != null) Destroy(skin);
        }

        private void OnGUI()
        {
            if (Event.current.type == EventType.Layout) laidOut = true;
            if (!laidOut) return;
            if (skin == null)
            {
                skin = Instantiate(GUI.skin);
                foreach (var style in new[] { skin.label, skin.button, skin.toggle, skin.textField })
                {
                    style.fontSize = 15;
                    style.normal.textColor = new Color(.94f, .96f, 1f);
                }
                skin.button.fixedHeight = ControlHeight;
                panel = new GUIStyle { padding = new RectOffset(20, 20, 14, 14) };
                heading = new GUIStyle(skin.label) { fontSize = 23, fontStyle = FontStyle.Bold };
            }

            var previousSkin = GUI.skin;
            var previousDepth = GUI.depth;
            try
            {
                GUI.skin = skin;
                GUI.depth = -100;
                var width = Mathf.Min(Width, Screen.width - 32);
                var height = Mathf.Min(Height, Screen.height - 24);
                var bounds = new Rect((Screen.width - width) / 2, (Screen.height - height) / 2, width, height);
                if (Event.current.type == EventType.Repaint)
                    Paint(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, .68f));
                GUI.ModalWindow(GetInstanceID(), bounds, _ =>
                {
                    if (Event.current.type == EventType.Repaint)
                    {
                        Paint(new Rect(0, 0, width, height), new Color(.09f, .11f, .15f));
                        Paint(new Rect(0, 0, width, 3), new Color(.32f, .77f, .87f));
                    }
                    GUILayout.Label(Title, heading);
                    Draw();
                }, GUIContent.none, panel);
            }
            finally
            {
                GUI.depth = previousDepth;
                GUI.skin = previousSkin;
            }
        }

        protected static void Paint(Rect rect, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        protected void Close(Action callback)
        {
            gameObject.SetActive(false);
            CMInputCallbackInstaller.ClearDisabledActionMaps(GetType(), disabled);
            Destroy(gameObject);
            callback();
        }
    }

    public sealed class TimingData
    {
        public double endBeat;
        public TempoRegion[] regions;
        public TimingMarker[] timings;
        public string sourceSha256;
        public int[] initiallyRemoved;
        public int[] proposedHandIds;
        public int simplifiedLinkCount;
        public TimingMerge[] merges;
        public TimingSection[] sections, subSections;
        public Dictionary<TimingMarker, TimingMarker[]> linkedMarkers, subLinkedMarkers;

        public static TimingData Parse(string json)
        {
            var root = JSON.Parse(json);
            if (root == null || !root.IsObject || !root["regions"].IsArray || !root["timings"].IsArray ||
                root["regions"].Count == 0 || root["timings"].Count == 0 || !root["sourceSha256"].IsString)
                throw new FormatException("The saved chart did not provide a complete timing preview.");
            var endBeat = Number(root["endBeat"]);
            if (endBeat <= 0) throw new FormatException("The timing preview has no playable beat range.");
            var regions = root["regions"].Children.Select(region => new TempoRegion
            {
                beat = Number(region["beat"]), seconds = Number(region["seconds"]),
                secondsPerBeat = Number(region["secondsPerBeat"])
            }).ToArray();
            for (int i = 0; i < regions.Length; i++)
                if (regions[i].secondsPerBeat <= 0 || (i > 0 && (regions[i].beat <= regions[i - 1].beat ||
                    regions[i].seconds <= regions[i - 1].seconds)))
                    throw new FormatException("The timing preview contains an invalid tempo region.");
            var timings = root["timings"].Children.Select(timing =>
            {
                var beat = Number(timing["beat"]);
                var count = Number(timing["count"]);
                if (beat > endBeat || count < 1 || count > int.MaxValue || count != Math.Truncate(count) ||
                    !timing["locked"].IsBoolean || !timing["hands"].IsArray || !timing["noteIds"].IsArray)
                    throw new FormatException("The timing preview contains an invalid note marker.");
                var hands = timing["hands"].Children.Select(Number).ToArray();
                var ids = timing["noteIds"].Children.Select(NoteId).ToArray();
                if (ids.Length != count || hands.Length != count || hands.Any(hand => hand != 0 && hand != 1))
                    throw new FormatException("The timing preview contains an invalid note hand.");
                return new TimingMarker
                {
                    beat = beat, locked = timing["locked"].AsBool,
                    hands = hands.Select(hand => (int)hand).ToArray(), noteIds = ids
                };
            }).ToArray();
            var removed = root["removedNoteIds"].Children.Select(NoteId).ToArray();
            var byId = timings.SelectMany(t => t.noteIds.Select(id => (id, timing: t)))
                .ToDictionary(note => note.id, note => note.timing);
            if (removed.Any(id => !byId.ContainsKey(id)))
                throw new FormatException("The timing preview refers to an unknown removed note.");
            var merges = root["merges"].Children.Select(item =>
            {
                int noteId = NoteId(item["noteId"]);
                int targetId = NoteId(item["targetId"]);
                if (!byId.TryGetValue(noteId, out var source) || !byId.TryGetValue(targetId, out var target) ||
                    source.noteIds.Length != 1 || target.noteIds.Length != 1 || source.hands[0] == target.hands[0])
                    throw new FormatException("The timing preview contains an invalid merge.");
                return new TimingMerge { noteId = noteId, targetId = targetId,
                    from = source.beat, to = target.beat };
            }).ToArray();
            var sections = ReadSections(root["sections"]);
            var scopes = root["subScopes"].Children.Select(item =>
                (start: Number(item["start"]), end: Number(item["end"]))).ToArray();
            if (scopes.Any(scope => scope.end <= scope.start))
                throw new FormatException("The timing preview contains an invalid local repeat scope.");
            var subSections = ReadSections(root["subSections"], scopes);
            foreach (var scope in subSections.GroupBy(section => section.Scope))
            {
                var range = scopes[scope.Key];
                var parent = sections.FirstOrDefault(section => section.Start <= range.start + 1e-6 &&
                    section.End >= range.end - 1e-6)?.Label ?? $"S{scope.Key + 1}";
                int groupIndex = 0;
                foreach (var group in scope.GroupBy(section => section.Group))
                {
                    int occurrence = 0;
                    foreach (var section in group)
                        section.Label = $"{parent}-{RepeatLetter(groupIndex)}{++occurrence}";
                    groupIndex++;
                }
            }
            var linked = ReadLinks(root["markerLinks"], timings);
            var subLinked = ReadLinks(root["subMarkerLinks"], timings, scopes);
            return new TimingData
            {
                endBeat = endBeat, regions = regions, timings = timings,
                sourceSha256 = root["sourceSha256"].Value, initiallyRemoved = removed, merges = merges,
                proposedHandIds = root["proposedHandIds"].Children.Select(NoteId).ToArray(),
                simplifiedLinkCount = root["simplifiedLinkCount"].AsInt,
                sections = sections, linkedMarkers = linked,
                subSections = subSections, subLinkedMarkers = subLinked
            };
        }

        private static TimingSection[] ReadSections(JSONNode items, (double start, double end)[] scopes = null)
        {
            var sections = items.Children.Select(item =>
            {
                var start = Number(item["start"]);
                var end = Number(item["end"]);
                if (end <= start)
                    throw new FormatException("The timing preview contains an invalid section.");
                int scope = -1;
                if (scopes != null)
                {
                    scope = NoteId(item["scope"]);
                    if (scope >= scopes.Length || start < scopes[scope].start - 1e-6 || end > scopes[scope].end + 1e-6)
                        throw new FormatException("A local repeat falls outside its section.");
                }
                return new TimingSection { Start = start, End = end, Group = NoteId(item["group"]), Scope = scope };
            }).ToArray();
            if (scopes == null)
                foreach (var group in sections.GroupBy(section => section.Group))
                {
                    int index = 0;
                    foreach (var section in group.OrderBy(section => section.Start))
                        section.Label = $"{RepeatLetter(group.Key)}{++index}";
                }
            return sections.OrderBy(section => section.Start).ToArray();
        }

        private static string RepeatLetter(int group) =>
            $"{(char)('A' + group % 26)}{(group >= 26 ? (group / 26 + 1) + "." : "")}";

        private static Dictionary<TimingMarker, TimingMarker[]> ReadLinks(JSONNode items, TimingMarker[] timings,
            (double start, double end)[] scopes = null)
        {
            var linked = new Dictionary<TimingMarker, TimingMarker[]>();
            foreach (var item in items.Children)
            {
                var scope = scopes == null ? 0 : NoteId(item["scope"]);
                if (scopes != null && scope >= scopes.Length)
                    throw new FormatException("The timing preview contains an invalid local repeat scope.");
                var markers = (scopes == null ? item : item["markers"]).Children.Select(NoteId).Select(index =>
                {
                    if (index >= timings.Length) throw new FormatException("The timing preview contains an invalid link.");
                    return timings[index];
                }).ToArray();
                if (markers.Length < 2 || markers.Any(marker => marker.noteIds.Length != markers[0].noteIds.Length))
                    throw new FormatException("The timing preview links markers with different note counts.");
                foreach (var marker in markers)
                {
                    if (scopes != null && (marker.beat < scopes[scope].start - 1e-6 || marker.beat >= scopes[scope].end))
                        throw new FormatException("A local timing link falls outside its section.");
                    if (!linked.TryAdd(marker, markers))
                        throw new FormatException("The timing preview links a marker more than once in the same edit scope.");
                }
            }
            return linked;
        }

        private static int NoteId(JSONNode node)
        {
            var value = Number(node);
            if (value < 0 || value > int.MaxValue || value != Math.Truncate(value))
                throw new FormatException("The timing preview contains an invalid note identifier.");
            return (int)value;
        }

        private static double Number(JSONNode node)
        {
            if (node == null || !node.IsNumber || double.IsNaN(node.AsDouble) || double.IsInfinity(node.AsDouble))
                throw new FormatException("The timing preview contains a missing or invalid beat value.");
            return node.AsDouble;
        }
    }

    public sealed class TempoRegion
    {
        public double beat, seconds, secondsPerBeat;
    }

    public sealed class TimingMarker
    {
        public double beat;
        public bool locked;
        public int[] hands, noteIds;
    }

    public sealed class TimingSection
    {
        public double Start, End;
        public int Group, Scope;
        public string Label;
    }

    public sealed class TimingMerge
    {
        public int noteId, targetId;
        public double from, to;
    }

    public sealed class TimingEdits
    {
        public string sourceSha256;
        public int[] removedNoteIds;
        public int[] proposedHandIds;
        public TimingMerge[] merges;

        public JSONObject ToJson()
        {
            var removed = new JSONArray();
            foreach (var id in removedNoteIds) removed.Add(id);
            var merged = new JSONArray();
            foreach (var merge in merges)
                merged.Add(new JSONObject { ["noteId"] = merge.noteId, ["targetId"] = merge.targetId });
            var proposed = new JSONArray();
            foreach (var id in proposedHandIds ?? Array.Empty<int>()) proposed.Add(id);
            return new JSONObject { ["sourceSha256"] = sourceSha256, ["removedNoteIds"] = removed,
                ["proposedHandIds"] = proposed,
                ["merges"] = merged };
        }
    }

    public sealed partial class TimingWindow : MonoBehaviour
    {
        private const string FollowPlayheadPreference = "bs-downmap.follow-playhead";
        private const string LinkSectionsPreference = "bs-downmap.link-timing-sections";
        private const double HoldPlaybackDuration = .3;
        private const double CueLookahead = .08;
        private static readonly Color DisabledMarkerColor = new Color(.42f, .45f, .50f);
        private static readonly Color MergeColor = new Color(.32f, .69f, 1f);
        private static readonly Color[] SectionColors =
        {
            new Color(.30f, .76f, .93f), new Color(.94f, .67f, .37f),
            new Color(.62f, .82f, .47f), new Color(.83f, .58f, .87f),
            new Color(.50f, .62f, .96f), new Color(.96f, .55f, .63f)
        };
        private readonly Stack<(Action undo, Action redo)> undo = new Stack<(Action, Action)>();
        private readonly Stack<(Action undo, Action redo)> redo = new Stack<(Action, Action)>();
        private TimingData data;
        private BeatClock beatClock;
        private Action<TimingEdits> finished;
        private HashSet<int> removed;
        private HashSet<int> inherited;
        private HashSet<TimingMerge> merges;
        private Dictionary<int, TimingMerge> mergeEndpoints;
        private CMInput input;
        private int subdivision, otherSubdivision;
        private bool heldPlayback;
        private InputControl lastPrecisionControl;
        private double lastPrecisionTime;
        private double start, heldStart;
        private float span = 16, zoom = 4;
        private double cursor, heldCursor, lastTickSeconds, playbackEnd;
        private double[] playbackTicks;
        private int nextTick, nextBeat;
        private bool playing;
        private double visualClockStart, visualClockSeconds, visualPosition, audioDriftTolerance;
        private bool linkSections, linkWithinSection, followPlayhead;
        private TimingSection hoveredSection;
        private float metronomeVolume, noteTickVolume;
        private TimingAudio audio;
        private int previousRenderInterval;

        public static void Show(string sourceFolder, string target, TimingData data, TimingEdits edits,
            Func<double, bool> removedAbove, Action<TimingEdits> callback)
        {
            var layout = DialogLayout.Load("EditNoteTimings");
            var dialog = PersistentUI.Instance.CreateNewDialogBox().WithTitle($"Edit {target} note timings");
            var window = dialog.gameObject.AddComponent<TimingWindow>();
            window.data = data;
            window.beatClock = new BeatClock(data.regions);
            window.finished = callback;
            window.followPlayhead = PlayerPrefs.GetInt(FollowPlayheadPreference, 0) != 0;
            window.linkSections = PlayerPrefs.GetInt(LinkSectionsPreference, 1) != 0;
            window.removed = new HashSet<int>(edits?.removedNoteIds ?? data.initiallyRemoved);
            window.inherited = new HashSet<int>(data.timings.Where(t => removedAbove(t.beat)).SelectMany(t => t.noteIds));
            window.merges = new HashSet<TimingMerge>(edits?.merges ?? data.merges);
            window.mergeEndpoints = data.merges.SelectMany(m => new[] { (id: m.noteId, merge: m), (id: m.targetId, merge: m) })
                .ToDictionary(endpoint => endpoint.id, endpoint => endpoint.merge);
            var editor = FindObjectOfType<AudioTimeSyncController>();
            if (editor != null && editor.IsPlaying) editor.TogglePlaying();
            window.audio = new TimingAudio(window.gameObject, editor);
            window.metronomeVolume = Mathf.Clamp01(Settings.Instance.MetronomeVolume);
            window.noteTickVolume = Mathf.Clamp01(Settings.Instance.NoteHitVolume);
            window.BuildDialog(dialog, layout);
            window.InstallInput(editor);
            dialog.Open();
            window.StartCoroutine(window.LoadAudio(sourceFolder));
        }

        private void OnEnable()
        {
            previousRenderInterval = OnDemandRendering.renderFrameInterval;
            SetPlaybackRendering(false);
        }

        private void SetPlaybackRendering(bool active)
        {
            // Keep the playhead responsive, and render fewer frames while the grid is idle.
            OnDemandRendering.renderFrameInterval = active ? previousRenderInterval : Math.Max(2, previousRenderInterval);
        }

        private IEnumerator LoadAudio(string folder)
        {
            yield return audio.Load(folder);
            if (!audio.Ready) yield break;
            playbackEnd = Math.Min(data.endBeat, Beat(audio.Length));
            yield return TimingWaveform.Generate(audio.Clip, result =>
            {
                waveform = result;
                timelineDirty = true;
            });
        }

        private void InstallInput(AudioTimeSyncController editor)
        {
            var settings = Settings.Instance;
            SetSubdivision(editor != null ? editor.GridMeasureSnapping : settings.CursorPrecisionA);
            otherSubdivision = Math.Max(1, subdivision == settings.CursorPrecisionB && subdivision != settings.CursorPrecisionA
                ? settings.CursorPrecisionA : settings.CursorPrecisionB);

            // The modal blocks the underlying editor's callbacks while sharing its live, rebound actions.
            input = CMInputCallbackInstaller.InputInstance;
            if (input == null) return;
            input.Playback.TogglePlaying.performed += OnPlayShortcut;
            input.Playback.TogglePlaying.canceled += OnPlayShortcut;
            input.Timeline.ChangeTimeandPrecision.performed += OnTimelineShortcut;
            input.Timeline.PreciselyChangeTimeandPrecision.performed += OnFinePrecisionShortcut;
            input.EventUI.SwapCursorInterval.performed += OnSwapPrecisionShortcut;
            input.Actions.UndoMethod1.performed += OnUndoShortcut;
            input.Actions.UndoMethod2.performed += OnUndoShortcut;
            input.Actions.RedoMethod1.performed += OnRedoShortcut;
            input.Actions.RedoMethod2.performed += OnRedoShortcut;
        }

        private void OnDisable()
        {
            OnDemandRendering.renderFrameInterval = previousRenderInterval;
            audio?.Stop();
            if (input != null) UninstallInput();
            if (finished == null) return;
            var callback = finished;
            finished = null;
            callback(appliedEdits);
        }

        private void UninstallInput()
        {
            input.Playback.TogglePlaying.performed -= OnPlayShortcut;
            input.Playback.TogglePlaying.canceled -= OnPlayShortcut;
            input.Timeline.ChangeTimeandPrecision.performed -= OnTimelineShortcut;
            input.Timeline.PreciselyChangeTimeandPrecision.performed -= OnFinePrecisionShortcut;
            input.EventUI.SwapCursorInterval.performed -= OnSwapPrecisionShortcut;
            input.Actions.UndoMethod1.performed -= OnUndoShortcut;
            input.Actions.UndoMethod2.performed -= OnUndoShortcut;
            input.Actions.RedoMethod1.performed -= OnRedoShortcut;
            input.Actions.RedoMethod2.performed -= OnRedoShortcut;
            input = null;
        }

        private void OnDestroy() => audio?.Release();

        private bool CanHandleShortcut => Application.isFocused && isActiveAndEnabled && !precisionInput.isFocused;

        private void OnUndoShortcut(InputAction.CallbackContext context)
        {
            if (CanHandleShortcut) StepHistory(true);
        }

        private void OnRedoShortcut(InputAction.CallbackContext context)
        {
            if (CanHandleShortcut) StepHistory(false);
        }

        private void CommitTimingEdit(Action revert, Action apply)
        {
            apply();
            undo.Push((revert, apply));
            redo.Clear();
            timelineDirty = true;
        }

        private void StepHistory(bool reverting)
        {
            var from = reverting ? undo : redo;
            if (from.Count == 0) return;
            if (playing) TogglePlayback();
            var edit = from.Pop();
            if (reverting)
            {
                edit.undo();
                redo.Push(edit);
            }
            else
            {
                edit.redo();
                undo.Push(edit);
            }
            timelineDirty = true;
        }

        private void OnPlayShortcut(InputAction.CallbackContext context)
        {
            if (context.performed && CanHandleShortcut && audio.Ready)
            {
                heldPlayback = !playing;
                heldCursor = cursor;
                heldStart = start;
                TogglePlayback();
            }
            else if (context.canceled)
            {
                if (heldPlayback && context.duration >= HoldPlaybackDuration) CancelHeldPlayback();
                heldPlayback = false;
            }
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused && heldPlayback) CancelHeldPlayback();
        }

        private void CancelHeldPlayback()
        {
            HaltPlayback();
            cursor = SnapBeat(heldCursor);
            start = ClampStart(heldStart);
        }

        private void OnTimelineShortcut(InputAction.CallbackContext context)
        {
            if (!CanHandleShortcut) return;
            var direction = Math.Sign(context.ReadValue<float>());
            if (direction == 0) return;
            if (input.Timeline.ChangePrecisionModifier.IsPressed())
                StepSubdivision(context, direction, input.Timeline.PreciseSnapModification.IsPressed());
            else
            {
                if (Settings.Instance.InvertScrollTime) direction = -direction;
                bool resume = playing;
                heldPlayback = false;
                cursor = Math.Max(0, Math.Min(data.endBeat, cursor + direction / (double)subdivision));
                FollowCursor();
                if (resume) StartPlayback(false);
            }
        }

        private void OnFinePrecisionShortcut(InputAction.CallbackContext context)
        {
            if (CanHandleShortcut) StepSubdivision(context, Math.Sign(context.ReadValue<float>()), true);
        }

        private void StepSubdivision(InputAction.CallbackContext context, int direction, bool fine)
        {
            // A single Ctrl+Shift wheel event can fire both native timeline actions.
            if (direction == 0 || (context.time == lastPrecisionTime && context.control == lastPrecisionControl)) return;
            lastPrecisionTime = context.time;
            lastPrecisionControl = context.control;
            if (Settings.Instance.InvertPrecisionScroll) direction = -direction;
            var next = fine ? (double)subdivision + direction : subdivision * (direction > 0 ? 2.0 : .5);
            SetSubdivision((int)Math.Max(1, Math.Min(64, Math.Round(next))));
        }

        private void OnSwapPrecisionShortcut(InputAction.CallbackContext context)
        {
            if (!CanHandleShortcut) return;
            int previous = subdivision;
            SetSubdivision(otherSubdivision);
            otherSubdivision = previous;
        }

        private void SetSubdivision(int value)
        {
            subdivision = Math.Max(1, value);
            precision.SetValueWithoutNotify(subdivision.ToString());
            timelineDirty = true;
        }

        private double SecondsPerBeat(double beat) => beatClock.SecondsPerBeat(beat);

        private double Seconds(double beat) => beatClock.Seconds(beat);

        private double Beat(double seconds) => beatClock.Beat(seconds);

        private IEnumerable<double> NoteCueTimes()
        {
            var settings = Settings.Instance;
            var audible = new[] { settings.Ding_Red_Notes, settings.Ding_Blue_Notes };
            foreach (var marker in data.timings)
                if (KeptHands(marker).Any(hand => audible[hand])) yield return Seconds(marker.beat);
        }

        private TimingMerge ActiveMerge(TimingMarker marker) =>
            marker.noteIds.Length == 1 && mergeEndpoints.TryGetValue(marker.noteIds[0], out var merge) &&
            merges.Contains(merge) && MergeAvailable(merge) ? merge : null;

        private bool MergeAvailable(TimingMerge merge) =>
            !inherited.Contains(merge.noteId) && !inherited.Contains(merge.targetId);

        private IEnumerable<int> KeptHands(TimingMarker marker)
        {
            var merge = ActiveMerge(marker);
            if (merge != null)
            {
                if (merge.targetId == marker.noteIds[0])
                {
                    yield return 0;
                    yield return 1;
                }
                yield break;
            }
            for (int i = 0; i < marker.noteIds.Length; i++)
                if (!removed.Contains(marker.noteIds[i]) && !inherited.Contains(marker.noteIds[i]))
                    yield return marker.hands[i];
        }

        private void UndoMerge(TimingMerge merge)
        {
            CommitTimingEdit(() => merges.Add(merge), () => merges.Remove(merge));
        }

        private void StartPlayback(bool restartAtEnd = true)
        {
            if (!audio.Ready || playbackEnd <= 0) return;
            if (cursor >= playbackEnd)
            {
                if (!restartAtEnd)
                {
                    PausePlayback();
                    return;
                }
                cursor = 0;
            }
            double seconds = Seconds(cursor);
            audio.Play(seconds);
            ResetVisualClock(audio.Position);
            EnsureCursorVisible();
            playing = true;
            SetPlaybackRendering(true);
            nextBeat = (int)Math.Ceiling(cursor - .000001);
            playbackTicks = NoteCueTimes().Where(time => time >= seconds - .000001).ToArray();
            nextTick = 0;
            lastTickSeconds = double.NegativeInfinity;
        }

        private void HaltPlayback()
        {
            playing = heldPlayback = false;
            SetPlaybackRendering(false);
            audio.Stop();
        }

        private double SnapBeat(double beat) =>
            Math.Max(0, Math.Min(data.endBeat, Math.Round(beat * subdivision, MidpointRounding.AwayFromZero) / subdivision));

        private void PausePlayback()
        {
            HaltPlayback();
            cursor = SnapBeat(cursor);
            EnsureCursorVisible();
        }

        private void TogglePlayback()
        {
            if (playing)
            {
                if (audio.Playing) cursor = Math.Min(playbackEnd, Beat(audio.Position));
                PausePlayback();
            }
            else StartPlayback();
        }

        private void Update()
        {
            if (!playing) return;
            if (!audio.Playing)
            {
                cursor = playbackEnd;
                PausePlayback();
                return;
            }
            var seconds = audio.Position;
            if (seconds >= Seconds(playbackEnd))
            {
                cursor = playbackEnd;
                PausePlayback();
                return;
            }

            cursor = Math.Min(playbackEnd, Beat(VisualSeconds(seconds)));
            if (followPlayhead) CenterPlaybackView();
            else EnsureCursorVisible();

            var endSeconds = Seconds(playbackEnd);
            double lead = audio.NoteLead;
            double speed = audio.Speed;
            while (nextTick < playbackTicks.Length)
            {
                var tick = playbackTicks[nextTick];
                var delay = (tick - seconds) / speed - lead;
                if (delay > CueLookahead) break;
                if (noteTickVolume > 0 && tick >= seconds - CueLookahead && tick <= endSeconds)
                {
                    audio.PlayNoteTick((tick - lastTickSeconds) * audio.SongBpm / 60, delay, noteTickVolume);
                    lastTickSeconds = tick;
                }
                nextTick++;
            }
            while (nextBeat <= playbackEnd)
            {
                var delay = (Seconds(nextBeat) - seconds) / speed;
                if (delay > CueLookahead) break;
                if (metronomeVolume > 0 && delay >= -CueLookahead) audio.PlayMetronome(delay, metronomeVolume);
                nextBeat++;
            }
        }

        private void ResetVisualClock(double seconds)
        {
            visualClockStart = Time.realtimeSinceStartupAsDouble;
            visualClockSeconds = visualPosition = seconds;
            AudioSettings.GetDSPBufferSize(out int buffer, out _);
            audioDriftTolerance = Math.Max(.1, 2d * buffer / audio.Clip.frequency);
        }

        private double VisualSeconds(double measured)
        {
            // AudioSource reports mixer blocks. Interpolate between them for display only.
            double predicted = visualClockSeconds + (Time.realtimeSinceStartupAsDouble - visualClockStart) * audio.Speed;
            double drift = measured - predicted;
            if (Math.Abs(drift) > audioDriftTolerance)
            {
                double limit = .02 * Time.unscaledDeltaTime;
                double correction = Math.Max(-limit, Math.Min(limit, drift));
                visualClockSeconds += correction;
                predicted += correction;
            }
            return visualPosition = Math.Max(visualPosition, predicted);
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused && playing && audio.Playing) ResetVisualClock(audio.Position);
        }

        private void EnsureCursorVisible()
        {
            if (cursor < start || cursor >= start + span)
                start = ClampStart(cursor);
        }

        private double ClampStart(double value) => Math.Max(0, Math.Min(value, Math.Max(0, data.endBeat - span)));
        private void FollowCursor() => start = ClampStart(cursor - span / 2);

        private void CenterPlaybackView()
        {
            // A fixed column lattice keeps the raster phase stable while the viewport follows playback.
            double step = span / (double)timeline.WaveformColumns;
            start = ClampStart(Math.Round((cursor - span / 2) / step) * step);
        }

        private void StopAtPageStart()
        {
            HaltPlayback();
            cursor = SnapBeat(start);
            start = ClampStart(Math.Min(start, cursor));
        }

        private void SeekOverview(double fraction)
        {
            HaltPlayback();
            cursor = SnapBeat(Beat(fraction * OverviewDuration));
            FollowCursor();
        }

        private double OverviewDuration => Math.Max(audio.Ready ? audio.Length : 0, Seconds(data.endBeat));

        private void EditMarker(TimingMarker marker, bool restore)
        {
            var links = linkWithinSection ? data.subLinkedMarkers : linkSections ? data.linkedMarkers : null;
            var related = links != null && links.TryGetValue(marker, out var peers) ? peers : new[] { marker };
            var editable = related.Where(t => (restore || !t.locked) && !inherited.Contains(t.noteIds[0]) &&
                ActiveMerge(t) == null).ToArray();
            var changed = editable.SelectMany(t => t.noteIds)
                .Where(id => removed.Contains(id) == restore).ToArray();
            if (changed.Length == 0) return;
            void Apply(bool add)
            {
                if (add) removed.UnionWith(changed);
                else removed.ExceptWith(changed);
            }
            CommitTimingEdit(() => Apply(restore), () => Apply(!restore));
        }

        private IEnumerable<TimingMarker> VisibleTimings(double end, double? from = null)
        {
            var timings = data.timings;
            double first = from ?? start;
            int low = 0, high = timings.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (timings[middle].beat < first) low = middle + 1;
                else high = middle;
            }
            for (int i = low; i < timings.Length && timings[i].beat <= end; i++)
                yield return timings[i];
        }

    }

    public sealed class GenerationWindow : DownmapWindow
    {
        private string message;
        private float fraction;
        protected override string Title => "BS Downmapper";
        protected override float Height => 120;

        public static GenerationWindow Show(string message)
        {
            var window = new GameObject("BS Downmapper Generation").AddComponent<GenerationWindow>();
            window.message = message;
            return window;
        }

        public void SetProgress(float value, string stage)
        {
            fraction = Mathf.Max(fraction, Mathf.Clamp01(value));
            message = stage;
        }

        protected override void Draw()
        {
            GUILayout.Label($"{message}  ({Mathf.RoundToInt(fraction * 100)}%)");
            var track = GUILayoutUtility.GetRect(80, 6, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;
            Paint(track, new Color(.16f, .21f, .27f));
            Paint(new Rect(track.x, track.y, track.width * fraction, track.height),
                new Color(.32f, .77f, .87f));
        }
        public void Finish() => Close(() => { });
    }

    internal sealed class SettingHint : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        internal static SettingHint ForSlider(SliderComponent slider)
        {
            var label = slider.GetComponentsInChildren<TMP_Text>(true).First(text =>
                text.transform.parent.parent == slider.transform &&
                !slider.Selectable.transform.IsChildOf(text.transform.parent));
            label.raycastTarget = true;
            return label.GetComponent<SettingHint>() ?? label.gameObject.AddComponent<SettingHint>();
        }

        internal string Hint;
        private Canvas tooltipCanvas;
        private bool previousOverride;
        private int previousOrder, previousLayer;
        private bool showing;

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (string.IsNullOrWhiteSpace(Hint) || PersistentUI.Instance == null) return;
            // Elevate only the native tooltip, not the entire persistent UI (loading screens, etc.).
            var field = typeof(PersistentUI).GetField("tooltipObject",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tooltip = field?.GetValue(PersistentUI.Instance) as GameObject;
            if (tooltip == null) return;
            tooltipCanvas = tooltip.GetComponent<Canvas>() ?? tooltip.AddComponent<Canvas>();
            previousOverride = tooltipCanvas.overrideSorting;
            previousOrder = tooltipCanvas.sortingOrder;
            previousLayer = tooltipCanvas.sortingLayerID;
            var dialogCanvas = GetComponentInParent<Canvas>();
            tooltipCanvas.overrideSorting = true;
            if (dialogCanvas != null)
            {
                tooltipCanvas.sortingLayerID = dialogCanvas.sortingLayerID;
                tooltipCanvas.sortingOrder = Mathf.Max(previousOrder, dialogCanvas.sortingOrder + 1);
            }
            PersistentUI.Instance.SetTooltip(Hint);
            PersistentUI.Instance.ShowTooltip();
            showing = true;
        }

        public void OnPointerExit(PointerEventData eventData) => Hide();
        private void OnDisable() => Hide();
        private void Hide()
        {
            if (!showing) return;
            showing = false;
            if (PersistentUI.Instance != null) PersistentUI.Instance.HideTooltip();
            if (tooltipCanvas == null) return;
            tooltipCanvas.overrideSorting = previousOverride;
            tooltipCanvas.sortingOrder = previousOrder;
            tooltipCanvas.sortingLayerID = previousLayer;
        }
    }

    internal sealed class GeneratorSettings
    {
        private sealed class Setting
        {
            public string Key, Label, Hint;
            public float Default, Value, Min, Max;
        }
        private readonly Dictionary<string, SliderComponent> sliders = new Dictionary<string, SliderComponent>();
        private readonly DialogLayout layout;
        private readonly ButtonComponent restore;
        private List<Setting> current = new List<Setting>();

        public GeneratorSettings(DialogBox dialog, DialogLayout layout, Action restoreModels)
        {
            this.layout = layout;
            foreach (string key in new[] { "height", "width", "crossover",
                "diagonalPreference", "parityAdherence", "rampAmount" })
            {
                var slider = dialog.AddComponent<SliderComponent>()
                    .WithCustomDisplayFormatter(value => value.ToString("0.00"));
                layout.Mount(slider, key);
                AlignSliderColumns(slider);
                sliders.Add(key, slider);
            }
            restore = layout.Mount(dialog.AddComponent<ButtonComponent>().WithLabel("Restore defaults").OnClick(() =>
            {
                foreach (var setting in current)
                    sliders[setting.Key].SetValueWithoutNotify(setting.Default);
                restoreModels();
            }), "RestoreDefaults");
        }

        public void Load(string json, string saved)
        {
            current = json == null ? new List<Setting>() : ReadSettings(json, saved);
            foreach (var pair in sliders)
                layout.Slot(pair.Key).gameObject.SetActive(false);
            foreach (var setting in current)
            {
                var slider = sliders[setting.Key];
                slider.WithLabel(setting.Label).WithSliderParams(setting.Min, setting.Max, .01f);
                slider.SetValueWithoutNotify(setting.Value);
                SettingHint.ForSlider(slider).Hint = setting.Hint;
                layout.Slot(setting.Key).gameObject.SetActive(true);
            }
            restore.Selectable.interactable = current.Count > 0;
        }

        internal void SetVisible(bool visible)
        {
            foreach (var pair in sliders)
                layout.Slot(pair.Key).gameObject.SetActive(visible && current.Any(setting => setting.Key == pair.Key));
            layout.Slot("RestoreDefaults").gameObject.SetActive(visible);
        }

        public string Capture()
        {
            var fields = new JSONObject();
            foreach (var setting in current)
            {
                float value = sliders[setting.Key].Value;
                if (!Mathf.Approximately(value, setting.Default)) fields[setting.Key] = value;
            }
            return fields.ToString();
        }

        internal static void AlignSliderColumns(SliderComponent slider, float valueWidth = 42)
        {
            var track = slider.Selectable.transform;
            var controls = track.parent;
            var controlColumn = controls.parent == slider.transform ? controls : controls.parent;
            var layout = controls.GetComponent<HorizontalLayoutGroup>();
            if (controlColumn.parent != slider.transform || layout == null) return;

            var row = slider.GetComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset();
            row.childForceExpandWidth = false;
            foreach (Transform column in slider.transform)
                SetWidth(column, 0, 1);
            layout.childControlWidth = true;
            layout.childForceExpandWidth = false;
            layout.spacing = 6;
            foreach (Transform control in controls)
            {
                bool isTrack = control == track;
                SetWidth(control, isTrack ? 0 : valueWidth, isTrack ? 1 : 0);
                if (isTrack) continue;

                var valueLayout = control.GetComponent<HorizontalLayoutGroup>();
                if (valueLayout != null)
                    valueLayout.enabled = false;
                var text = control.GetComponentInChildren<TMP_Text>();
                if (text != null)
                {
                    DialogLayout.Fill(text.rectTransform, (RectTransform)control);
                    text.alignment = TextAlignmentOptions.MidlineRight;
                    text.margin = Vector4.zero;
                    text.enableWordWrapping = false;
                }
            }
        }

        internal static void SetWidth(Transform transform, float width, float flexibleWidth)
        {
            var size = transform.GetComponent<LayoutElement>() ?? transform.gameObject.AddComponent<LayoutElement>();
            size.minWidth = size.preferredWidth = width;
            size.flexibleWidth = flexibleWidth;
        }

        private static List<Setting> ReadSettings(string json, string saved)
        {
            var profile = JSON.Parse(json);
            if (profile == null || !profile.IsObject || !profile["geometry"].IsObject ||
                !profile["difficultyRamp"].IsObject || !profile["difficultyRamp"]["enabled"].IsBoolean)
                throw new FormatException("The difficulty profile is missing its geometry or difficulty ramp settings.");
            var geometry = profile["geometry"];
            var ramp = profile["difficultyRamp"];
            var overrides = string.IsNullOrEmpty(saved) ? null : JSON.Parse(saved);
            var controls = new List<Setting>();

            void Add(string key, string label, string hint, JSONNode node, float min = 0, float max = 1)
            {
                var value = ReadNumber(node, key, min, max);
                controls.Add(new Setting { Key = key, Label = label, Hint = hint, Default = value, Min = min, Max = max,
                    Value = overrides != null && overrides.HasKey(key) ? ReadNumber(overrides[key], key, min, max) : value });
            }

            Add("height", "Vertical spread", "Higher values allow more top-row placements with adequate recovery.", geometry["height"]);
            Add("width", "Horizontal spread", "Higher values favor the leftmost lane for the left hand and the rightmost lane for the right hand.", geometry["width"]);
            Add("crossover", "Crossover spread", "Higher values make opposite-side reaches more likely when the clearance checks pass.", geometry["crossover"]);
            Add("diagonalPreference", "Angle simplification", "Higher values favor cardinal directions. Linked note directions stay fixed.", profile["diagonalPreference"], 0, 10);
            Add("parityAdherence", "Parity adherence", "Higher values prioritize alternating swing flow. At 100%, the engine aims for zero new parity errors. Lower values favor spaced downstroke repeats.", profile["parityAdherence"]);
            Add("rampAmount", "Late difficulty ramp", "Softens placement and arrow preferences later in the song. Zero disables the ramp.",
                ramp["enabled"].AsBool ? ramp["amount"] : new JSONNumber(0));
            return controls;
        }

        private static float ReadNumber(JSONNode node, string key, float min, float max)
        {
            if (node == null || !node.IsNumber || !(node.AsDouble >= min && node.AsDouble <= max))
                throw new FormatException($"The difficulty profile needs a valid number for {key}.");
            return node.AsFloat;
        }

    }
}
