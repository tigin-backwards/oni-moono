# The DeCrew colony's other management screens: Skills (J), Research (R, scrolled with the wheel)
# and Schedule (.), each closed with Escape.
SAVE="${ONI_M9_SAVE:-$root/tests/e2e/fixtures/decrew.sav}"
FRAMES=2120
DUMPS="1740 1890 2040"
ARGS=(--input 620:click:891,176 --input 1600:click:511,567
      --input 1660:key:J --input 1760:key:Escape
      --input 1800:key:R --input 1840:move:512,400 --input 1850:scroll:0,-5 --input 1910:key:Escape
      --input 1950:key:Period)
