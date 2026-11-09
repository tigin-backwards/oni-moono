# The research screen (measurement): opened, then zoomed out with the wheel, dumped before and
# after the zoom.
SAVE="${ONI_M9_SAVE:-$root/tests/e2e/fixtures/decrew.sav}"
FRAMES=1760
DUMPS="1645 1660 1680 1700 1715 1740"
ARGS=(--input 620:click:891,176 --input 1600:click:511,567
      --input 1640:key:R --input 1690:move:512,400 --input 1710:scroll:0,-5)
