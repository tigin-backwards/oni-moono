// vma.dll for the Windows player (scripts/build-native-libs.sh): the Vulkan Memory Allocator's
// implementation with its C API exported, configured as BRUTAL's 2026.10.3 vma.dll is (the
// same 73 exports, no vulkan-1.dll import): the Vulkan functions come from the
// vkGetInstanceProcAddr and vkGetDeviceProcAddr that Brutal.Vulkan.Vma passes in
// VmaAllocatorCreateInfo::pVulkanFunctions, and everything else is VMA's default for the
// Vulkan 1.4 headers. Built with the static C runtime (/MT), so its std::mutex is the one it
// was compiled with, whatever MSVCP140.dll the machine has (#126).
#define VK_NO_PROTOTYPES
#define VMA_STATIC_VULKAN_FUNCTIONS 0
#define VMA_DYNAMIC_VULKAN_FUNCTIONS 1
#define VMA_CALL_PRE __declspec(dllexport)
#define VMA_IMPLEMENTATION
#include "vk_mem_alloc.h"
