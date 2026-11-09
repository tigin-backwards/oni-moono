# The DeCrew colony's management screens, opened with their hotkeys and closed with Escape:
# Priorities (L, scrolled with the wheel), Vitals (V) and Consumables (F).
SAVE="${ONI_M9_SAVE:-$root/tests/e2e/fixtures/decrew.sav}"
FRAMES=2080
DUMPS="1720 1860 2000"
ARGS=(--input 620:click:891,176 --input 1600:click:511,567
      --input 1640:key:L --input 1680:move:512,420 --input 1690:scroll:0,-3 --input 1740:key:Escape
      --input 1780:key:V --input 1880:key:Escape
      --input 1920:key:F)
