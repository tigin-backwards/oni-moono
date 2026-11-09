# The DeCrew colony running (performance): phase times every 60 frames (--frame-stats), the HUD
# only, to compare the canvas update ("post-late update") with each package.
SAVE="${ONI_M9_SAVE:-$root/tests/e2e/fixtures/decrew.sav}"
FRAMES=2200
DUMPS="2150"
ARGS=(--input 620:click:891,176 --input 1600:click:511,500 --frame-stats)
