# The colony scenario on Vulkan with every shader's GLCore programs compiled to SPIR-V
# (ONI_VULKAN_ROUTE=glcore, ARCHITECTURE §3.5), compared with the colony's references: Klei's
# world, its property textures and light buffer, as OpenGL draws them.
source "$root/scripts/m9-scenarios/colony.sh"
REFERENCE=colony
VULKAN=1
ENV=(ONI_VULKAN_ROUTE=glcore)
ARGS+=(--renderer vulkan)
REQUIRE=("renderer: Vulkan" "'Klei/TexturePage': its GLCore programs compiled to SPIR-V (ONI_VULKAN_ROUTE=glcore)")
