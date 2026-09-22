# EmotionCat artwork

The user supplied the white silver-tabby bongo-cat reference. The production family preserves its rounded front-view silhouette, three gray forehead stripes, pink ears and cheeks, dark chocolate outline, and curled tail on the viewer's right.

All runtime animation frames are transparent 512 × 384 RGBA PNGs. The common head and tail remain fixed across emotions and poses. The desk line is drawn by the application at native y = 310. Down-paw bottoms are around y = 330; raised paws reach about y = 184 with bottoms at y = 326. Render every frame into the same rectangle; do not fit each image to its nontransparent bounds.

The eight labels are neutral, angry, love, excited, sad, surprised, sleepy, and confused. Each has idle, left, right, and both states; left/right refer to the viewer's side. A raised paw shows pink toe beans. The lifted side has no resting paw underneath.

`sprites/body_<emotion>.png` are pawless body layers. Four `sprites/paw_<side>_<state>.png` layers have the same full canvas. The assembled runtime files in `frames/` are the preferred rendering contract. `sprites/<emotion>.png` are idle previews for settings. Small `paw.png`, `paw_left.png`, and `paw_right.png` files are retained as source components and are not full-frame states.

Use smooth bilinear or bicubic downsampling for desktop display, preserve alpha, and avoid mipmap or color-key conversion. The preview contact sheet shows all expressions plus complete neutral and love pose sequences at half native size.
