# Source snapshot verification

Validated on Windows on November 12, 2026. These checks apply to the complete `LoLTranslator-source.zip` source distribution, not the selected browsable mirrors.

- Managed solution Release build: passed, 0 warnings / 0 errors.
- Console regression suite: exit 0.
- OCR shadow adapter: 110/110.
- Continuous runtime trace/change gate: 50/50.
- Runtime event contract: 54/54.
- Dispatch: 28/28.
- Safe presentation: 44/44.
- Capture/view timing: 18/18.
- Other data, semantic and rendering checks reported no failures. Seed readiness remains 0/17; that is an existing data qualification result, not a failing software test.

No live match, GPU performance or real model translation quality was revalidated for this publication. Native OCR dependencies and model weights must be obtained separately.

Publication changes: MIT license and handoff documentation; source/diagnostic exclusions; public runtime-term packaging paths; repository download-link target and its regression expectation. Two reviewed text-only normalization fixtures were inspected and retained without changing their hashes. No raw screenshot/chat session or player identifier file was included.

To reproduce, extract the archive and run the README commands inside `LoLTranslator`. An offline machine may need pre-cached NuGet packages; the initial connected restore may also query NuGet advisory metadata.
