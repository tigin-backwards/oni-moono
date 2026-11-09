# Save and load: the DeCrew colony saved from the pause menu (over its own file, confirmed),
# then that save loaded back through the pause menu's load screen (the colony, its newest save),
# and the reloaded colony captured.
SAVE="${ONI_M9_SAVE:-$root/tests/e2e/fixtures/decrew.sav}"
FRAMES=3200
DUMPS="1720 1960 2100 3150"
TOLERANCE=0.06
ARGS=(--input 620:click:891,176 --input 1600:click:511,500 --input 1640:key:Escape --input 1700:click:511,234
      --input 1760:click:511,396 --input 1880:click:511,316 --input 1990:click:300,190
      --input 2120:click:946,494 --input 3050:click:511,500)
KEPT_MAX=2 # the first colony's targets stay referenced after the reload (kept by reachability, as Unity keeps them)
# The colony's inventory, which captures barely show. The first load (Resume Game, the clock
# running) counts it: rations and pinned resource amounts. The reload goes through the load
# screen, which stops the clock (Time.timeScale 0) and leaves the game paused: in the original no
# pickupable becomes fetchable until the game runs, so rations read 0 kcal and the pinned
# amounts "..." (measured with a reload probe mod in both players).
DUMP_MATCH=('1720:CurrentRations#1.*text="[1-9][0-9,]* kcal"' '3150:CurrentRations#1.*text="0 kcal"'
            '3150:PinnedResourcesPanel#.*ValueLabel#.*text="\.\.\."')
DUMP_ABSENT=('1720:PinnedResourcesPanel#.*ValueLabel#.*text="\.\.\."')
