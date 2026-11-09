# The Database search field's caret blinking (measurement): the colony scenario, dumped every 15
# frames after the last keystroke.
SAVE="${ONI_M9_SAVE:-$root/tests/e2e/fixtures/decrew.sav}"
FRAMES=2160
DUMPS="2010 2025 2040 2055 2070 2085 2100 2115 2130 2145"
ARGS=(--input 620:click:891,176 --input 1600:click:511,567 --input 1750:click:946,28 --input 1880:click:918,160 --input 1950:text:oxygenx --input 2000:key:Backspace)
