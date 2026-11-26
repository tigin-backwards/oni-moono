# The sources of the native libraries scripts/build-native-libs.sh builds for the Windows player
# (sourced): each a GitHub archive at a pinned commit and its SHA-256. The commits are the ones
# BRUTAL's 2026.10.3 packages name for the libraries they ship (their LICENSES notices), and
# Vulkan-Headers the release BRUTAL's Vulkan bindings are generated from.
# shellcheck shell=bash disable=SC2034  # read by the script that sources it
# NAME REPOSITORY COMMIT SHA256 (of https://codeload.github.com/REPOSITORY/tar.gz/COMMIT)
native_sources=(
    # Vulkan Memory Allocator v3.3.0-39-ge722e57 (3.4.0-development), MIT
    "vma GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator e722e57c891a8fbe3cc73ca56c19dd76be242759 eaedbc97c0b098af1bdbd003a48e9bc19d994bac2dbae2776dcd8f39e55cf9db"
    # Vulkan-Headers v1.4.335, Apache-2.0 OR MIT (VMA's include of vulkan.h)
    "vulkan-headers KhronosGroup/Vulkan-Headers 2fa203425eb4af9dfc6b03f97ef72b0b5bcb8350 66e0dc7932203def85d94bf65a4fa3a3b29e61366713404f0465cdeb02e6ffd3"
    # shaderc v2026.2-dev, Apache-2.0; its DEPS pin the three below
    "shaderc google/shaderc 422c78aa4809afae803fdda9220c30e8c24daccf ab05dddace80402361cd415c63a73fb5b4d2c58bd43b2fa1c0b531ea1f6d7c7d"
    # glslang, BSD-3-Clause and others (its LICENSE.txt)
    "glslang KhronosGroup/glslang dcf1aaa6fd7dc2081f17aa0a4f1590a76473d961 ad2f6f0c30dd36d893900c10703f2c39be30372c3778183afd27b7d8d6aea708"
    # SPIRV-Tools v2026.2-dev, Apache-2.0
    "spirv-tools KhronosGroup/SPIRV-Tools a52828d672ab1622c308d0a6fc99630b79561609 a357b2fce9907e357c36f1ebf6f4e79ba3f4ad5afe7f22cb2b6e04416f659d0f"
    # SPIRV-Headers, MIT
    "spirv-headers KhronosGroup/SPIRV-Headers 869266ad9e6050197d87cf0a22aab59abf7ad008 1aee8219bc4edfd9533a610ddc7144602ca785b680674af008fd20d922fcb0f4"
)
