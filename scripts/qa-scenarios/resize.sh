# The menu at 1024x768, resized to 800x600 and back to 1024x768 (Screen.SetResolution, as the
# graphics options do), then a colony (DeCrew) resumed and resized in play: the game's own
# KCanvasScaler exception on an in-game resize is expected (the original player raises it too).
SAVE="${ONI_M9_SAVE:-$root/tests/e2e/fixtures/decrew.sav}"
FRAMES=1900
DUMPS="500 600 1700 1880"
ARGS=(--input 450:resize:800,600 --input 550:resize:1024,768
      --input 620:click:891,176 --input 1600:click:511,567 --input 1750:resize:800,600)
# The in-game resize recreates the world's targets (OpenGL's line, then Vulkan's).
REQUIRE=("multi-target framebuffer deleted with its textures || vulkan: multi-target render texture released")
