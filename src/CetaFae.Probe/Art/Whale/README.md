# Whale art runtime candidate

Derived from the user-directed sequence-v2 artwork, exported by `tools/Export-WhaleArt.ps1`.
This is a trial integration, not final visual acceptance. No third-party reference-video character is included.
The original editable/generated source layers remain in the local art workspace; do not replace them with these reduced exports.

Each transparent PNG is an8x4 atlas of96px cells. Local layers share the original400x400 center anchor; never trim them independently.
Draw rear water, visible whale, front water, then spout. Horizontal poses are normalized for runtime mirroring.
`motion.json` preserves25fps timing, independent whale/water transforms and simultaneous coverage/input progress from scene frames240–319.
The spout is cropped at the shared scene anchor; no mock window, baked text or raster input field is shipped.

Runtime uses a bounded page cache and a static idle pose. Native controls, outlines, clipped wash, timing interruption and menu remain host mechanisms.
Artwork was produced for this project through the user's art workflow; provider/source rights have not received a separate legal review.

## Refined drop-water import

`tools/Import-RefinedWater.ps1` imports the20261031 art-refinement delivery. `drop-whale-00/01.png` preserve the existing whale identity and approved action study, with35registered96px cells at25fps; these source poses face left and use the opposite normalization from the earlier scene atlas. `drop-motion.json` contains nozzle offsets only. `rest-water.png` is the384px transparent B02 rear-water source, paired with its editable `rest-water.svg`. The runtime traces its contour at source resolution and continuously expands it into the native shell. No sample window, grid or text is embedded.

Spray trajectories, receiving/fill timing and the surf reference are implemented as native vector paths in RefinedWater/WhaleWater/WindowWash. The spout-fill-v4 and window-wash-v1 renderers are the local editable references; runtime code is the distributed editable implementation. This import removes the old stretched small-spout placeholder from the live drop path. Existing idle/drag/return atlases are retained. Source rights are unchanged; no external reference character was imported. Native visual acceptance remains pending.
