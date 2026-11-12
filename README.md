# LoLTranslator

An experimental Windows desktop translator for League of Legends chat, built with C#, WPF and .NET 10. It captures a selected screen region, recognizes chat locally, translates ordinary messages with local Qwen3 through Ollama, and displays results in a companion overlay.

**Development is paused. This is a research prototype, not a finished player release. Contributions and forks are welcome; ongoing support and response times are not guaranteed.**

## 项目状态

这是英雄联盟聊天实时翻译原型。已实现区域截图、OCR、消息去重与队列、本地模型翻译、游戏术语保护和悬浮译文窗口。目前暂停主动开发，欢迎 fork 或接手。真实对局中的缩写、口语、混合语言、识别误差和翻译质量仍需完善；自动测试通过不代表实战翻译已经可靠。

## What is included

- WPF control panel, region selection and screen capture.
- Native PP-OCRv5 integration, plus experimental Windows OCR and DirectML implementations.
- Continuous message tracking, duplicate suppression, ordering and stale-result handling.
- Local Ollama adapter for `qwen3:8b`, with thinking disabled and protected terminology handling.
- SlangGraph semantic matching, rendering and terminology packs.
- Console regression checks and experimental evaluation tools.

## Build and check

Requirements: Windows 11 x64, the .NET 10 SDK, and access to NuGet for the initial restore. These commands build the managed application; they do **not** install the native OCR runtime or model weights.

```powershell
dotnet restore LoLTranslator.slnx
dotnet build LoLTranslator.slnx -c Release --no-restore
dotnet run --project tests/LoLTranslator.DataChecks -c Release --no-build
dotnet run --project src/LoLTranslator.App -c Release --no-build
```

The regression suite uses local fixtures and fake providers; it does not need a running game or download a model. Some experimental runners refer to private local screenshots or historical research artifacts that are intentionally absent. They are not part of the reproducible baseline above.

## Runtime dependencies

Building the WPF application alone is not enough to start live translation:

1. Install Ollama separately and obtain `qwen3:8b`. The prototype checks the pinned model digest in `ControlledShadowQwen3Profile.cs`; a different model revision may be rejected. Models are not bundled or downloaded automatically by the application.
2. Build the native PP-OCR sidecar and obtain the detector, recognizer and character dictionary from their upstream sources. See [native setup](tools/ppocr-windows-sidecar/README.md) and [GPU prerequisites](docs/ocr-runtime-gpu-prerequisites-guide.md). This is a development workflow requiring Visual Studio C++ build tools, CMake, Paddle/OpenCV and compatible CUDA/cuDNN for the GPU path.
3. Configure paths if needed:

```powershell
$env:LOLTRANSLATOR_PPOCRV5_SIDECAR_PATH = 'C:\path\to\loltranslator-ppocr-sidecar.exe'
$env:LOLTRANSLATOR_PPOCRV5_DET_MODEL_DIR = 'C:\path\to\PP-OCRv5_mobile_det'
$env:LOLTRANSLATOR_PPOCRV5_REC_MODEL_DIR = 'C:\path\to\PP-OCRv5_server_rec'
```

Place `character_dict.txt` in the recognizer model directory. The current live path reports unavailable if its native components are missing; it does not automatically substitute a different OCR engine. Historical setup scripts include machine-specific defaults that must be changed for your system.

## Known limitations

- Ordinary conversation, abbreviations, fragments and ambiguous game slang can be translated incorrectly. There is no finished quality benchmark for live matches.
- OCR errors, chat-envelope parsing and expired results can cause missing or incorrect translations.
- GPU OCR and Qwen compete with the game for resources. Earlier local timings are not performance guarantees.
- Terminology snapshots are patch-specific and can become stale. Candidate/review labels are retained; open-sourcing does not promote their verification status.
- Some inherited Chinese text has encoding damage. It is retained here rather than silently rewritten during release preparation.
- No ready-to-run binary or bundled models are provided in this source release.

## Privacy and scope

This public source export excludes real-match screenshots, raw OCR/chat diagnostic sessions, private review records, build outputs, downloaded third-party sources and model weights. Two screenshot-derived optional diagnostic inputs were removed from the public build; runtime terminology is packaged under `data/runtime`.

The application uses screen capture rather than game memory injection. Diagnostics can record selected screen regions and chat locally when explicitly enabled. Review and redact them before sharing. The feedback configuration has no deployed endpoint. This project has no Riot endorsement or certification; users should consult applicable game/platform rules before use.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/LoLTranslator.App` | Desktop UI, capture, OCR, translation and overlay |
| `src/SlangGraph.Core` | Semantic graph and rendering logic |
| `tests/LoLTranslator.DataChecks` | Offline console regression suite |
| `data` | Runtime terminology, seed examples and regression packs |
| `tools` | Native sidecar source/build helpers and experimental evaluators |
| `docs` | Architecture, data model and setup notes |

## Contributing and license

Small, reproducible fixes are welcome. Include a synthetic example, expected output and relevant test results. Do not submit player identifiers, private chat, screenshots, credentials or model binaries. See [CONTRIBUTING.md](CONTRIBUTING.md).

Original project code is available under the [MIT License](LICENSE). Riot-derived terminology and third-party dependencies retain their respective rights and terms; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). League of Legends and related names belong to Riot Games.
