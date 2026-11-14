# Third-party notices

The ChroMapper build embeds libsndfile, QuickJS-NG, BSMap, and Valibot license texts as `BsDownmap.Licenses.*` resources. It also produces `THIRD-PARTY-NOTICES.txt` beside the release DLL using the license files supplied for that build. Distribute that file with the DLL.

The audio decoder is supplied separately to the build; retain its exact version, provenance, and accompanying notices. A compatible external Windows x64 decoder can be selected with `BS_DOWNMAP_AUDIO_DECODER`. Review any additional components included in the selected decoder build before release.

ChroMapper and Unity assemblies are build references supplied by the installed editor. They are not copied into the plugin release by the build script. Python and Node development dependencies are not the standalone plugin runtime.

BS Downmapper is licensed under [MIT](LICENSE), Copyright (c) 2026 Skyler. Bundled dependencies retain their own licenses.
