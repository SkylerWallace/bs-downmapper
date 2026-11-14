using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace BsDownmap.ChroMapper
{
    // Use the mapper's audio assets and scheduler with a separate transport for the unsaved timing draft.
    internal sealed class TimingAudio
    {
        private readonly AudioSource song;
        private readonly AudioUtil notePlayer, metronomePlayer;
        private readonly SoundList[] soundLists;
        private readonly AudioClip metronomeClip;
        private readonly float shortCutThreshold;
        private AudioClip ownedClip;

        public TimingAudio(GameObject owner, AudioTimeSyncController editor)
        {
            song = owner.AddComponent<AudioSource>();
            song.playOnAwake = false;
            AudioUtil.MakeSourceNonDimensional(song, false);
            var source = editor != null ? editor.SongAudioSource : null;
            if (source != null)
            {
                song.pitch = source.pitch;
                song.mute = source.mute;
                song.outputAudioMixerGroup = source.outputAudioMixerGroup;
            }
            song.volume = Settings.Instance.SongVolume;
            notePlayer = owner.AddComponent<AudioUtil>();
            metronomePlayer = owner.AddComponent<AudioUtil>();

            // These assets are serialized on ChroMapper's scene components and have no public getters.
            var ding = UnityEngine.Object.FindObjectOfType<DingOnNotePassingGrid>();
            soundLists = SceneField<SoundList[]>(ding, "soundLists");
            shortCutThreshold = SceneField<float>(ding, "thresholdInNoteTime");
            var metronome = UnityEngine.Object.FindObjectOfType<MetronomeHandler>();
            metronomeClip = SceneField<AudioClip>(metronome,
                metronome != null && metronome.CowBell ? "cowbellSound" : "metronomeSound");
        }

        private static T SceneField<T>(MonoBehaviour component, string name) =>
            component != null && component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(component) is T value ? value : default(T);

        public IEnumerator Load(string folder)
        {
            var current = BeatSaberSongContainer.Instance;
            if (current != null && current.Info != null && current.LoadedSong != null &&
                !string.IsNullOrWhiteSpace(current.Info.Directory) &&
                string.Equals(NormalizePath(current.Info.Directory), NormalizePath(folder), Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                SongBpm = current.Info.BeatsPerMinute;
                song.clip = current.LoadedSong;
                yield break;
            }
            var info = BeatSaberSongUtils.GetInfoFromFolder(folder.Trim());
            if (info == null) yield break;
            SongBpm = info.BeatsPerMinute;
            yield return BeatSaberSongExtensions.LoadAudio(info, clip =>
            {
                ownedClip = clip;
                song.clip = clip;
            }, info.SongTimeOffset, null);
        }

        private static string NormalizePath(string folder) =>
            Path.GetFullPath(folder.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        public AudioClip Clip => song.clip;
        public bool Ready => song.clip != null && song.clip.samples > 0;
        public bool Playing => song.isPlaying;
        public double Position => song.timeSamples / (double)song.clip.frequency;
        public double Length => song.clip.samples / (double)song.clip.frequency;
        public float SongBpm { get; private set; }
        public float Speed => song.pitch;
        public bool HasNoteTicks => soundLists != null;
        public bool HasMetronome => metronomeClip != null;
        // ChroMapper starts the Slice asset early to align the cut in the clip with the beat.
        public double NoteLead => Settings.Instance.NoteHitSound == (int)HitSounds.Slice ? .18 : 0;

        public void Play(double seconds)
        {
            Stop();
            song.timeSamples = (int)Math.Max(0, Math.Min(song.clip.samples - 1, Math.Round(seconds * song.clip.frequency)));
            song.Play();
        }

        public void Stop()
        {
            song.Stop();
            StopNoteTicks();
            StopMetronome();
        }

        public void StopNoteTicks() => notePlayer.StopOneShot();
        public void StopMetronome() => metronomePlayer.StopOneShot();

        public void PlayNoteTick(double gapInSongBpmBeats, double delay, float volume)
        {
            int sound = Settings.Instance.NoteHitSound;
            if (soundLists == null || sound < 0 || sound >= soundLists.Length || soundLists[sound] == null) return;
            var clip = soundLists[sound].GetRandomClip(gapInSongBpmBeats < shortCutThreshold);
            if (clip != null) notePlayer.PlayOneShotSound(clip, volume, 1, (float)Math.Max(0, delay));
        }

        public void PlayMetronome(double delay, float volume)
        {
            if (metronomeClip == null) return;
            metronomePlayer.PlayOneShotSound(metronomeClip, volume, 1, (float)Math.Max(0, delay));
        }

        public void Release()
        {
            if (ownedClip != null) UnityEngine.Object.Destroy(ownedClip);
        }
    }
}
