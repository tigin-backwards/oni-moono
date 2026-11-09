# Every overlay of a base-game colony (DeCrew), by its hotkey, each captured: oxygen, power,
# temperature, materials, light, plumbing, ventilation, decor, germs, farming, rooms, exosuits,
# automation, conveyors; then the overlay closed (F1 again on the last open one).
SAVE="${ONI_M9_SAVE:-$root/tests/e2e/fixtures/decrew.sav}"
FRAMES=2420
DUMPS="1640 1700 1760 1820 1880 1940 2000 2060 2120 2180 2240 2300 2360 2410"
TOLERANCE=0.08
ARGS=(--input 620:click:891,176 --input 1600:click:511,500
      --input 1620:key:F1 --input 1680:key:F2 --input 1740:key:F3 --input 1800:key:F4 --input 1860:key:F5
      --input 1920:key:F6 --input 1980:key:F7 --input 2040:key:F8 --input 2100:key:F9 --input 2160:key:F10
      --input 2220:key:F11
      --input 2279:keydown:LeftShift --input 2280:key:F1 --input 2283:keyup:LeftShift
      --input 2339:keydown:LeftShift --input 2340:key:F2 --input 2343:keyup:LeftShift
      --input 2390:key:Escape)
KEPT_MAX=2 # the coloured overlay view adds a three-target set beside the two-target one
