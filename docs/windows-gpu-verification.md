# Windows GPU / Korean verification

Verified locally on 2026-09-23: Windows 11 x64, NVIDIA GeForce RTX 5060 Ti,
driver 610.47, .NET Framework 4.8, for the Windows GPU update after v2.0.0.
These measurements cover the local hardware; the release workflow separately
builds and verifies the distributed archives.

## Behavior

- Model inference requires a hardware GPU through DirectML. Hardware filtering,
  high-performance device selection and `session.disable_cpu_ep_fallback=1`
  prevent software-GPU or CPU EP execution.
- Missing/unsupported runtime, GPU initialization failure or a GPU inference error
  disables analysis, stops text capture for analysis and shows the settings
  warning plus a tray notification. Paw animation remains available.
- Known profanity, including common Korean abbreviations and separated spellings,
  selects angry with priority. Clear Korean expressions and chat abbreviations
  use explicit rules; other text reaches the GPU model. Both paths require a
  successfully initialized GPU session. The UI identifies which path was used.
- Existing default prompts migrate to the Korean prompt; custom prompts remain.
- The GPU model has real attention/choice padding masks. It does not treat padding
  as text. The model retains the 1,024-token / 16-choice limits.

## Results

| Check | Result |
| --- | --- |
| Core/configuration/rules/unavailable GPU | 52 assertions passed |
| Original tokenizer sequences | 44 exact matches |
| Original labeled model benchmark | 36/40, unchanged from the release benchmark |
| Added Korean response cases | 75/75 |
| GPU execution trace | 21 DirectML fused-node executions; 0 CPU EP nodes |
| GPU model inference, 20 measured runs | 25.5 ms median |
| Loaded inference-session idle CPU, 8 seconds | 0.016% of total logical CPU capacity |
| Final installed application idle CPU, 20 seconds | 0.020% of total logical CPU capacity |
| Final installed application working set | 797.4 MiB |
| Final installed application dedicated GPU memory | 1,015.7 MiB |
| Global character/Hangul tests | 27 passed |
| Sprite mapping and rendering tests | 50 passed |
| Actual OS keyboard events in owned fixture | Korean love/anger, profanity -> angry, and contextual GPU inference passed |
| Idle and modifier-key behavior | No inference requests |
| CPU-only runtime fixture | Warning state; analysis disabled; profanity cannot bypass the GPU requirement |
| Build preparation | Reproduced the tested GPU model byte for byte |
| Workflow files | Parsed as valid YAML; hosted CI not run in this session |

The 75 Korean examples are a development regression set, not an independent
accuracy estimate. Novel slang, sarcasm, ambiguous phrases, complicated negation
and unlisted obfuscations can still be misclassified. Rules and model responses
are visible separately in the app.

## Reproduction

```powershell
./scripts/prepare-windows.ps1
./build.ps1
./tests/run-core-tests.ps1 -Golden tests/onnx-golden.json -Integration
./tests/run-korean-tests.ps1
./tests/run-gpu-tests.ps1
./tests/run-input-tests.ps1
./tests/run-visual-tests.ps1
# Stop EmotionCat first; input is sent only to the owned test window.
./tests/run-live-input-tests.ps1
```

GPU model SHA-256:
`12c70367fc1211121844c1d6e41b9395961c9fa0b89348df094375e32a7365d3`

Final executable SHA-256:
`d1e701ca8cd37b328fa6a1944c7640d356e1a5e9b37c14d25f76ff6f86482d77`

Windows packaging includes only the GPU model, tokenizer, native DLLs, sprites
and licenses. Python and its packages are build-time tools, not app dependencies.
The macOS source and existing macOS release use their separate implementation;
this verification covers the Windows installation.
