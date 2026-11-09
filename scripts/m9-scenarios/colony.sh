# A large colony (DeCrew, base game): resumed from the main menu, the mod dialog dismissed, the
# Database opened and searched (text input through Event.PopEvent).
SAVE="${ONI_M9_SAVE:-$root/tests/e2e/fixtures/decrew.sav}"
FRAMES=2300
DUMPS="1550 1700 1950 2200"
ARGS=(--input 620:click:891,176 --input 1600:click:511,567 --input 1750:click:946,28 --input 1880:click:918,160 --input 1950:text:oxygenx --input 2000:key:Backspace)
