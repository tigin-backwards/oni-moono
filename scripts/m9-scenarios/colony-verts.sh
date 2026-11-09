# Measurement: the colony scenario's Database search frame, with the vertices of the elements
# whose path contains $VERTS listed.
SAVE="${ONI_M9_SAVE:-$root/tests/e2e/fixtures/decrew.sav}"
FRAMES=1960
DUMPS="1950"
ARGS=(--input 620:click:891,176 --input 1600:click:511,567 --input 1750:click:946,28 --input 1880:click:918,160 --input 1950:text:oxygenx --dump-ui-vertices "${VERTS:-Codex}")
