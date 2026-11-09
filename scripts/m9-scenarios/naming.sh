# Renaming the colony: its name field (a KInputTextField) activated from the top-left panel,
# typed into, then submitted with Enter.
SAVE="${ONI_M9_SAVE:-$root/tests/e2e/fixtures/decrew.sav}"
FRAMES=1960
DUMPS="1800 1950"
ARGS=(--input 620:click:891,176 --input 1600:click:511,567 --input 1700:click:130,78 --input 1760:text:New --input 1790:key:Backspace --input 1850:key:Return)
