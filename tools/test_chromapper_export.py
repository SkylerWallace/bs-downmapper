from contextlib import redirect_stdout
from io import StringIO
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from analyze_standard_spreads import load, write
from beatmap_io import metadata, sha
from chromapper_export import generate_spread, install_difficulty, main, profile_for, publish_spread, timing_conflicts


class GeneratorProfileTests(unittest.TestCase):
    def test_horizontal_spread_override_is_independent_of_height(self):
        with tempfile.TemporaryDirectory() as workspace:
            settings = Path(workspace) / "settings.json"
            for difficulty in ("Easy", "Normal", "Hard", "Expert"):
                with self.subTest(difficulty=difficulty):
                    default = profile_for(difficulty, None)["geometry"]
                    write(settings, {"width": 0.65})
                    geometry = profile_for(difficulty, settings)["geometry"]
                    self.assertEqual(geometry, {**default, "width": 0.65})
            for value in (-0.1, 1.1, True):
                with self.subTest(value=value):
                    write(settings, {"width": value})
                    with self.assertRaisesRegex(ValueError, "width"):
                        profile_for("Easy", settings)


class InstallDifficultyTests(unittest.TestCase):
    def setUp(self):
        workspace = tempfile.TemporaryDirectory()
        self.addCleanup(workspace.cleanup)
        root = Path(workspace.name)
        self.song, self.reports = root / "song", root / "reports"
        self.info = {
            "song": {"title": "Example"},
            "audio": {"songFilename": "song.ogg", "audioDataFilename": "AudioData.dat"},
            "difficultyBeatmaps": [
                {
                    "characteristic": "Standard",
                    "difficulty": "ExpertPlus",
                    "beatmapDataFilename": "ExpertPlus.beatmap.dat",
                    "lightshowDataFilename": "lights.dat",
                },
            ],
        }
        self.generated = {
            "characteristic": "Standard",
            "difficulty": "Expert",
            "beatmapDataFilename": "Expert.beatmap.dat",
            "lightshowDataFilename": "lights.dat",
        }
        write(self.reports / "candidate" / "Info.dat", {"difficultyBeatmaps": [self.generated]})
        self.chart = self.reports / "candidate" / self.generated["beatmapDataFilename"]
        write(self.chart, {"colorNotes": [{"b": 4}]})
        write(self.reports / "frozen.json", {"candidateSha256": sha(self.chart)})

    def install(self, target_hash="missing"):
        return install_difficulty(self.song, self.reports, "Expert", sha(self.song / "Info.dat"), target_hash)

    def test_add_difficulty_without_overwriting_an_orphan_file(self):
        write(self.song / "Info.dat", self.info)
        orphan = self.song / self.chart.name
        orphan.write_text("orphan", encoding="utf-8")
        target = self.install()
        info = load(self.song / "Info.dat")
        self.assertEqual(info["song"], self.info["song"])
        self.assertEqual(info["difficultyBeatmaps"][0], self.info["difficultyBeatmaps"][0])
        self.assertEqual(metadata(info, "Expert")["beatmapDataFilename"], target.name)
        self.assertEqual(sha(target), sha(self.chart))
        self.assertEqual(orphan.read_text(encoding="utf-8"), "orphan")

    def test_approval_cannot_be_reused_after_the_target_changes(self):
        self.info["difficultyBeatmaps"].append(self.generated)
        write(self.song / "Info.dat", self.info)
        existing = self.song / self.chart.name
        existing.write_text("before approval", encoding="utf-8")
        before = sha(existing)
        existing.write_text("after approval", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "target difficulty changed"):
            self.install(before)
        self.assertEqual(existing.read_text(encoding="utf-8"), "after approval")
        self.assertEqual(load(self.song / "Info.dat"), self.info)

    def test_roll_back_existing_chart_when_info_commit_fails(self):
        self.info["difficultyBeatmaps"].append(self.generated)
        info_path = self.song / "Info.dat"
        write(info_path, self.info)
        existing = self.song / self.chart.name
        existing.write_text("original", encoding="utf-8")
        replace = os.replace

        def fail_info(source, destination):
            if destination == info_path:
                raise PermissionError("file in use")
            replace(source, destination)

        with patch("chromapper_export.os.replace", side_effect=fail_info):
            with self.assertRaises(PermissionError):
                self.install(sha(existing))
        self.assertEqual(existing.read_text(encoding="utf-8"), "original")
        self.assertEqual(load(info_path), self.info)
        self.assertFalse((self.song / ".bs-downmap.lock").exists())

    def test_install_rechecks_a_lower_chart_and_keeps_target_on_conflict(self):
        self.info["difficultyBeatmaps"].extend(
            [
                self.generated,
                {
                    "characteristic": "Standard",
                    "difficulty": "Hard",
                    "beatmapDataFilename": "Hard.beatmap.dat",
                    "lightshowDataFilename": "lights.dat",
                },
            ]
        )
        info_path = self.song / "Info.dat"
        write(info_path, self.info)
        existing = self.song / self.chart.name
        write(existing, {"colorNotes": [{"b": 4}, {"b": 5}]})
        before = sha(existing)
        write(self.song / "Hard.beatmap.dat", {"colorNotes": [{"b": 5}]})
        with self.assertRaisesRegex(ValueError, "1 required by Hard"):
            self.install(before)
        self.assertEqual(sha(existing), before)
        self.assertEqual(load(info_path), self.info)
        self.assertFalse((self.song / ".bs-downmap.lock").exists())

    def test_reports_both_missing_higher_and_required_lower_timings(self):
        saved = {"ExpertPlus": [4, 8, 9], "Expert": [4, 9], "Easy": [9]}
        self.assertEqual(
            timing_conflicts(saved, "Hard", [4, 8]),
            [
                {"difficulty": "Expert", "higher": True, "beats": [8]},
                {"difficulty": "Easy", "higher": False, "beats": [9]},
            ],
        )

    def test_review_saved_target_timings_and_check_edits_against_lower_chart(self):
        self.info["audio"]["bpm"] = 120
        self.info["coverImageFilename"] = "cover.png"
        hard = {**self.generated, "difficulty": "Hard", "beatmapDataFilename": "Hard.beatmap.dat"}
        self.info["difficultyBeatmaps"].extend((self.generated, hard))
        write(self.song / "Info.dat", self.info)
        write(self.song / "AudioData.dat", {
            "songFrequency": 48000, "bpmData": [{"sb": 0, "eb": 8, "si": 0, "ei": 192000}],
        })
        for chart, beats in zip(self.info["difficultyBeatmaps"], ([4, 5, 6, 7], [4, 5, 6], [5]), strict=True):
            write(self.song / chart["beatmapDataFilename"], {
                "version": "3.3.0",
                "colorNotes": [{"b": beat, "x": 1, "c": 0, "d": 1} for beat in beats],
            })

        args = ["--source", str(self.song), "--source-difficulty", "ExpertPlus",
                "--difficulty", "Expert", "--rhythm-mode", "authored", "--rhythm-difficulty", "Expert"]

        def run(*options):
            with redirect_stdout(StringIO()) as output:
                main([*args, *options])
            return json.loads(output.getvalue().split(": ", 1)[1])

        preview = run("--preview")
        self.assertEqual([marker["beat"] for marker in preview["timings"]], [4, 5, 6])
        self.assertEqual(preview["sourceSha256"], sha(self.song / self.chart.name))
        edits = self.song / "edits.json"
        write(edits, {"sourceSha256": preview["sourceSha256"], "removedNoteIds": [0, 2]})
        self.assertEqual(run("--check-timings", "--timing-edits", str(edits)), [])
        write(edits, {"sourceSha256": preview["sourceSha256"], "removedNoteIds": [1]})
        self.assertEqual(run("--check-timings", "--timing-edits", str(edits)),
                         [{"difficulty": "Hard", "higher": False, "beats": [5]}])

    def test_batch_publishes_a_shared_removal_only_when_the_lower_target_is_selected(self):
        hard = {**self.generated, "difficulty": "Hard", "beatmapDataFilename": "Hard.beatmap.dat"}
        self.info["difficultyBeatmaps"].extend((self.generated, hard))
        staged = self.reports / "staged"
        names = ["ExpertPlus.beatmap.dat", "Expert.beatmap.dat", "Hard.beatmap.dat"]
        for folder in (self.song, staged):
            write(folder / "Info.dat", self.info)
            for name in names:
                beats = [4, 5] if folder == self.song or name == names[0] else [4]
                write(folder / name, {"colorNotes": [{"b": beat} for beat in beats]})
        expert_hash, hard_hash = (sha(self.song / name) for name in names[1:])
        info_hash = sha(self.song / "Info.dat")
        with self.assertRaisesRegex(ValueError, "required by Hard"):
            publish_spread(self.song, staged, self.reports, info_hash, {"Expert": expert_hash})
        self.assertEqual(sha(self.song / names[1]), expert_hash)
        self.assertEqual(sha(self.song / names[2]), hard_hash)

        publish_spread(self.song, staged, self.reports, info_hash, {"Expert": expert_hash, "Hard": hard_hash})
        for name in names:
            self.assertEqual(sha(self.song / name), sha(staged / name))
        self.assertEqual(sha(self.reports / "previous" / names[1]), expert_hash)
        self.assertEqual(sha(self.reports / "previous" / names[2]), hard_hash)

    def test_batch_passes_pattern_sources_and_models_per_target(self):
        write(self.song / "Info.dat", self.info)
        plan = {
            "infoSha256": sha(self.song / "Info.dat"),
            "foundation": "ExpertPlus",
            "targets": [
                {"difficulty": "Expert", "targetSha256": "missing", "mode": "best-guess",
                 "model": "nested-priority-v1"},
                {"difficulty": "Hard", "targetSha256": "missing", "mode": "best-guess",
                 "foundation": "Expert", "model": "nested-priority-v1"},
            ],
        }
        with (
            patch.dict(os.environ, {"LOCALAPPDATA": str(self.reports)}),
            patch("chromapper_export.main") as build,
            patch("chromapper_export.publish_spread"),
        ):
            generate_spread(self.song, plan)
            for call, expected in zip(build.call_args_list, (
                ("ExpertPlus", "nested-priority-v1"),
                ("Expert", "nested-priority-v1"),
            ), strict=True):
                args = call.args[0]
                self.assertEqual(args[args.index("--source-difficulty") + 1], expected[0])
                self.assertEqual(args[args.index("--timing-model") + 1], expected[1])

        plan["targets"][1]["foundation"] = "OneSaber"
        with self.assertRaisesRegex(ValueError, "Standard pattern source for each target"):
            generate_spread(self.song, plan)
