# Artwork provenance

Source reference: user-provided image `2026-09-22_15-16-21_txt2img_2 (1).png`, copied into `source/user-reference.png`. User supplied it for this project; no independent claim about ownership or third-party licensing is made.

Generation tool: the built-in `image_gen.imagegen` tool, used for the neutral hero, seven facial variants, the raised paw, background extraction, and removal of resting paws from the shared body. No external image API or fallback CLI was used. The exact prompts are in `generation-prompts.json`. Generated source images are preserved in `source/`; the raw PNGs retain their generated metadata where provided.

Some direct edits rendered an unwanted checkerboard instead of real transparency. Successful follow-up background extraction images supplied genuine alpha. Final production files do not use a checkerboard background. The confused variant's face region, entirely inside the opaque white cat, was reused from the generated expression image; its exterior background is never used.

Deterministic assembly: `tools/asset-build.ps1` scales the generated hero/body/face art to 512 × 384, blends face crops inside the shared pawless body, slices the resting paws from the hero, mirrors/places the generated raised paw, and composites four complete pose frames for each emotion. It creates no new facial artwork. Sharing one body and four paw layers prevents outline drift and doubled paws during animation.

Visual inspection: `contact-sheet.png` reviewed at native half scale against a pale mauve desktop background. All eight expressions are distinct; all four poses have separate visible paw states, stable head/tail placement, and no visible face-crop seams. File dimensions, alpha, outer-edge transparency, content bounds, and SHA-256 checks are recorded in `asset-manifest.json`.

The application's own rendering and keyboard interaction need separate integration testing; asset verification does not by itself verify the application behavior.
