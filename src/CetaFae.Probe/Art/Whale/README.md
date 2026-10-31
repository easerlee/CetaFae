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
